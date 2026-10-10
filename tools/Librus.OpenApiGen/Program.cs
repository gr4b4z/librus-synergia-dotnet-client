// Generates NativeAOT-friendly C# DTOs, a System.Text.Json source-generation context and typed GET
// operations from the librus-sdk OpenAPI documents.
//
//   dotnet run --project tools/Librus.OpenApiGen -- openapi/api_v3_openapi.json src/Librus.Api/Generated
//
// Mapping rules (the documents are "loose": most fields are anyOf string|number|null):
//   * anyOf [string, number] (+null)       -> LibrusId (string-backed, accepts both)
//   * ApiRef / ApiRefOrJson                -> ApiRef (Id/Url + extension data)
//   * UnknownRecord / mixed anyOf          -> JsonElement
//   * SynergiaEntity(List)                 -> SynergiaEntity (extension data only) / List<SynergiaEntity>
//   * inline objects                       -> nested DTO classes named after the property
//   * every DTO keeps [JsonExtensionData] so unknown fields survive round-trips.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Librus.OpenApiGen <openapi.json> <output-dir> [namespace] [prefix] [--no-operations]");
    return 2;
}

var inputPath = args[0];
var outputDir = args[1];
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var ns = positional.Length > 2 ? positional[2] : "Librus.Api.Models";
var prefix = positional.Length > 3 ? positional[3] : "LibrusApi";

var doc = JsonNode.Parse(File.ReadAllText(inputPath))!.AsObject();
var gen = new Generator(doc, ns, prefix);
gen.Run();
Directory.CreateDirectory(outputDir);
File.WriteAllText(Path.Combine(outputDir, $"{prefix}Models.g.cs"), gen.ModelsSource());
File.WriteAllText(Path.Combine(outputDir, $"{prefix}JsonContext.g.cs"), gen.ContextSource());
if (gen.HasOperations && !args.Contains("--no-operations"))
{
    File.WriteAllText(Path.Combine(outputDir, $"{prefix}Operations.g.cs"), gen.OperationsSource());
}

Console.WriteLine($"Generated {gen.ClassCount} DTOs and {(args.Contains("--no-operations") ? 0 : gen.OperationCount)} operations into {outputDir}");
return 0;

internal sealed class Generator(JsonObject doc, string ns, string prefix)
{
    // Schemas mapped to hand-written primitives in Librus.Api (LibrusId.cs / ApiRef.cs).
    private static readonly Dictionary<string, string> BuiltIns = new()
    {
        ["ApiRef"] = "ApiRef",
        ["ApiRefOrJson"] = "ApiRef",
        ["UnknownRecord"] = "JsonElement",
        ["NullableUnknownRecord"] = "JsonElement",
        ["SynergiaEntity"] = "SynergiaEntity",
        ["SynergiaEntityList"] = "List<SynergiaEntity>",
    };

    // Inline object names that a plain singularization would get wrong.
    private static readonly Dictionary<string, string> NameOverrides = new()
    {
        ["TimetablesResponse.Timetable"] = "TimetableLesson",
        ["SynergiaMeResponse.Me"] = "SynergiaMe",
        ["AuthPhotosResponse.data"] = "AuthPhotoPayload",
        ["AuthPhotoResponse.data"] = "AuthPhotoPayload",
        ["AuthPhotoPayload.photo"] = "AuthPhoto",
    };

    private readonly JsonObject _schemas = doc["components"]?["schemas"]?.AsObject() ?? [];
    private readonly Dictionary<string, ClassModel> _classes = new(StringComparer.Ordinal);
    private readonly List<string> _classOrder = [];
    private readonly List<string> _responseTypes = [];
    private readonly List<Operation> _operations = [];

    public int ClassCount => _classes.Count;
    public int OperationCount => _operations.Count;
    public bool HasOperations => _operations.Count > 0;

    public void Run()
    {
        foreach (var (name, schema) in _schemas)
        {
            if (BuiltIns.ContainsKey(name) || schema is null)
            {
                continue;
            }

            if (IsObjectLike(schema.AsObject()))
            {
                EnsureClass(name, schema.AsObject(), contextName: null, preferredName: name);
            }
        }

        foreach (var (path, item) in doc["paths"]!.AsObject())
        {
            foreach (var (method, opNode) in item!.AsObject())
            {
                if (method != "get")
                {
                    continue;
                }

                _operations.Add(ParseOperation(path, opNode!.AsObject()));
            }
        }
    }

    private Operation ParseOperation(string path, JsonObject op)
    {
        var id = op["operationId"]!.GetValue<string>();
        var parameters = new List<Param>();
        foreach (var p in op["parameters"]?.AsArray() ?? [])
        {
            var po = p!.AsObject();
            var schema = po["schema"]?.AsObject() ?? [];
            var type = schema["type"]?.GetValue<string>();
            var csType = type switch
            {
                "integer" => "int",
                "boolean" => "bool",
                _ when schema["format"]?.GetValue<string>() == "date" => "DateOnly",
                _ => "string",
            };
            parameters.Add(new Param(po["name"]!.GetValue<string>(), po["in"]!.GetValue<string>(), csType));
        }

        var content = op["responses"]?["200"]?["content"]?.AsObject();
        string? responseType = null;
        var binary = false;
        if (content is not null)
        {
            if (content["application/json"]?["schema"]?["$ref"]?.GetValue<string>() is { } r)
            {
                responseType = RefName(r);
                if (!_responseTypes.Contains(responseType))
                {
                    _responseTypes.Add(responseType);
                }
            }
            else
            {
                binary = true;
            }
        }

        return new Operation(id, path, op["summary"]?.GetValue<string>() ?? id, parameters, responseType, binary);
    }

    private static bool IsObjectLike(JsonObject schema) =>
        schema["type"]?.GetValue<string>() == "object" && schema["properties"] is JsonObject
        || schema["anyOf"] is JsonArray any && any.All(a => a is JsonObject ao && (ao["$ref"] is not null || ao["properties"] is not null));

    private string EnsureClass(string schemaKey, JsonObject schema, string? contextName, string preferredName)
    {
        var fingerprint = schema.ToJsonString();
        var name = preferredName;
        if (_classes.TryGetValue(name, out var existing))
        {
            if (existing.Fingerprint == fingerprint)
            {
                return name;
            }

            name = (contextName ?? "") + preferredName;
            if (_classes.TryGetValue(name, out existing) && existing.Fingerprint == fingerprint)
            {
                return name;
            }
        }

        var model = new ClassModel(name, fingerprint, schemaKey);
        _classes[name] = model;
        _classOrder.Add(name);

        foreach (var (propName, propSchema, required) in CollectProperties(schema))
        {
            var (type, nullable) = ResolveType(propSchema, name, propName);
            model.Properties.Add(new PropertyModel(propName, type, nullable, required));
        }

        return name;
    }

    /// <summary>Flattens plain objects and top-level anyOf unions (e.g. list-or-status responses).</summary>
    private IEnumerable<(string Name, JsonObject Schema, bool Required)> CollectProperties(JsonObject schema)
    {
        if (schema["anyOf"] is JsonArray branches)
        {
            var seen = new HashSet<string>();
            var requiredEverywhere = new HashSet<string>();
            var first = true;
            var collected = new List<(string, JsonObject)>();
            foreach (var branch in branches)
            {
                var b = Deref(branch!.AsObject());
                var req = (b["required"]?.AsArray() ?? []).Select(x => x!.GetValue<string>()).ToHashSet();
                if (first)
                {
                    requiredEverywhere.UnionWith(req);
                    first = false;
                }
                else
                {
                    requiredEverywhere.IntersectWith(req);
                }

                foreach (var (pn, ps) in b["properties"]?.AsObject() ?? [])
                {
                    if (seen.Add(pn))
                    {
                        collected.Add((pn, ps!.AsObject()));
                    }
                }
            }

            foreach (var (pn, ps) in collected)
            {
                yield return (pn, ps, requiredEverywhere.Contains(pn));
            }

            yield break;
        }

        var required = (schema["required"]?.AsArray() ?? []).Select(x => x!.GetValue<string>()).ToHashSet();
        foreach (var (pn, ps) in schema["properties"]?.AsObject() ?? [])
        {
            yield return (pn, ps!.AsObject(), required.Contains(pn));
        }
    }

    private JsonObject Deref(JsonObject schema) =>
        schema["$ref"]?.GetValue<string>() is { } r ? _schemas[RefName(r)]!.AsObject() : schema;

    private static string RefName(string reference) => reference[(reference.LastIndexOf('/') + 1)..];

    private (string Type, bool Nullable) ResolveType(JsonObject schema, string owner, string propName)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
        {
            var refName = RefName(reference);
            if (BuiltIns.TryGetValue(refName, out var builtIn))
            {
                return (builtIn, refName.StartsWith("Nullable", StringComparison.Ordinal));
            }

            var target = _schemas[refName]!.AsObject();
            return IsObjectLike(target) ? (EnsureClass(refName, target, null, refName), false) : ResolveType(target, owner, propName);
        }

        if (schema["anyOf"] is JsonArray any)
        {
            var options = any.Select(o => o!.AsObject()).ToList();
            var nullable = options.RemoveAll(o => o["enum"] is JsonArray e && e.Count == 1 && e[0] is null) > 0;
            if (options.Count == 1)
            {
                var (t, n) = ResolveType(options[0], owner, propName);
                return (t, n || nullable);
            }

            var primitiveTypes = options.Select(o => o["type"]?.GetValue<string>()).ToHashSet();
            if (options.All(o => o["type"] is not null && o["properties"] is null) && primitiveTypes.SetEquals(["string", "number"]))
            {
                return ("LibrusId", nullable);
            }

            return ("JsonElement", nullable);
        }

        var type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "string":
                return ("string", false);
            case "boolean":
                return ("bool", false);
            case "integer":
                return ("long", false);
            case "number":
                return (IsIntegerLike(propName) ? "long" : "double", false);
            case "array":
                {
                    var items = schema["items"]?.AsObject() ?? [];
                    var itemName = NameOverrides.GetValueOrDefault($"{owner}.{propName}") ?? Singularize(Pascal(propName));
                    var (itemType, _) = items["type"]?.GetValue<string>() == "object" && items["properties"] is not null
                        ? (EnsureClass(itemName, items, owner, itemName), false)
                        : ResolveType(items, owner, propName);
                    return ($"List<{itemType}>", false);
                }

            case "object":
                if (schema["properties"] is JsonObject props && props.Count > 0)
                {
                    var objName = NameOverrides.GetValueOrDefault($"{owner}.{propName}") ?? Pascal(propName);
                    if (objName == owner)
                    {
                        objName += "Details";
                    }

                    return (EnsureClass(objName, schema, owner, objName), false);
                }

                if (schema["additionalProperties"] is JsonObject additional && additional.Count > 0)
                {
                    var nested = Unwrap(additional, out var depth);
                    var leafName = NameOverrides.GetValueOrDefault($"{owner}.{propName}") ?? Singularize(Pascal(propName));
                    var leafType = nested["type"]?.GetValue<string>() == "object" && nested["properties"] is not null
                        ? EnsureClass(leafName, nested, owner, leafName)
                        : ResolveType(nested, owner, propName).Type;
                    for (var i = 0; i < depth; i++)
                    {
                        leafType = $"List<{leafType}>";
                    }

                    return ($"Dictionary<string, {leafType}>", false);
                }

                return ("JsonElement", false);
            default:
                return ("JsonElement", false);
        }
    }

    private static JsonObject Unwrap(JsonObject schema, out int arrayDepth)
    {
        arrayDepth = 0;
        while (schema["type"]?.GetValue<string>() == "array" && schema["items"] is JsonObject items)
        {
            arrayDepth++;
            schema = items;
        }

        return schema;
    }

    private static bool IsIntegerLike(string name) =>
        name == "Id" || name.EndsWith("Id", StringComparison.Ordinal) || name is "Semester" or "LessonNo" or "UnreadMessages" or "lastModification";

    internal static string Pascal(string name)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch))
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }

        var result = sb.ToString();
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string Singularize(string name)
    {
        if (name.EndsWith("ies", StringComparison.Ordinal))
        {
            return name[..^3] + "y";
        }

        if (name.EndsWith("sses", StringComparison.Ordinal) || name.EndsWith("ches", StringComparison.Ordinal))
        {
            return name[..^2];
        }

        if (name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal))
        {
            return name[..^1];
        }

        return name + "Item";
    }

    private static bool IsValueType(string type) => type is "bool" or "long" or "double" or "int" or "LibrusId" or "JsonElement";

    public string ModelsSource()
    {
        var sb = Header();
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        foreach (var name in _classOrder)
        {
            var model = _classes[name];
            sb.AppendLine();
            sb.AppendLine($"/// <summary>Generated from OpenAPI schema <c>{model.SchemaKey}</c>.</summary>");
            sb.AppendLine($"public sealed partial class {model.Name}");
            sb.AppendLine("{");
            var usedNames = new HashSet<string> { "AdditionalProperties" };
            foreach (var p in model.Properties)
            {
                var csName = Pascal(p.JsonName);
                if (csName == model.Name)
                {
                    csName += "Value";
                }

                while (!usedNames.Add(csName))
                {
                    csName += "_";
                }

                string declaration;
                if (p.Required && !p.Nullable && p.Type != "JsonElement")
                {
                    var init = p.Type switch
                    {
                        "string" => " = \"\";",
                        _ when p.Type.StartsWith("List<", StringComparison.Ordinal) || p.Type.StartsWith("Dictionary<", StringComparison.Ordinal) => " = [];",
                        _ when IsValueType(p.Type) => "",
                        _ => " = new();",
                    };
                    declaration = $"public {p.Type} {csName} {{ get; set; }}{init}";
                }
                else
                {
                    declaration = $"public {p.Type}? {csName} {{ get; set; }}";
                }

                sb.AppendLine($"    [JsonPropertyName(\"{p.JsonName}\")]");
                sb.AppendLine($"    {declaration}");
                sb.AppendLine();
            }

            sb.AppendLine("    /// <summary>Fields not described by the OpenAPI document.</summary>");
            sb.AppendLine("    [JsonExtensionData]");
            sb.AppendLine("    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }");
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    public string ContextSource()
    {
        var sb = Header();
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Source-generated (NativeAOT-safe) serializer metadata for every generated DTO.</summary>");
        sb.AppendLine("[JsonSourceGenerationOptions(");
        sb.AppendLine("    WriteIndented = true,");
        sb.AppendLine("    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,");
        sb.AppendLine("    NumberHandling = JsonNumberHandling.AllowReadingFromString)]");
        foreach (var name in _classOrder)
        {
            sb.AppendLine($"[JsonSerializable(typeof({name}))]");
        }

        sb.AppendLine("[JsonSerializable(typeof(JsonElement))]");
        sb.AppendLine($"public sealed partial class {prefix}JsonContext : JsonSerializerContext;");
        return sb.ToString();
    }

    public string OperationsSource()
    {
        var sb = Header();
        sb.AppendLine($"using {ns};");
        sb.AppendLine();
        sb.AppendLine("namespace Librus.Api;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Typed GET operations generated from the OpenAPI document (raw api.librus.pl/3.0 surface).</summary>");
        sb.AppendLine("public sealed partial class LibrusChildClient");
        sb.AppendLine("{");
        foreach (var op in _operations)
        {
            var methodName = Pascal(op.Id) + "Async";
            var pathParams = op.Parameters.Where(p => p.In == "path").ToList();
            var queryParams = op.Parameters.Where(p => p.In == "query").ToList();
            var signature = new List<string>();
            signature.AddRange(pathParams.Select(p => $"string {Camel(p.Name)}"));
            signature.AddRange(queryParams.Select(p => $"{p.Type}? {Camel(p.Name)} = null"));
            signature.Add("CancellationToken cancellationToken = default");

            var pathExpr = "\"" + op.Path.TrimStart('/') + "\"";
            if (pathParams.Count > 0)
            {
                var interpolated = op.Path.TrimStart('/');
                foreach (var p in pathParams)
                {
                    interpolated = interpolated.Replace("{" + p.Name + "}", "{Uri.EscapeDataString(" + Camel(p.Name) + ")}", StringComparison.Ordinal);
                }

                pathExpr = "$\"" + interpolated + "\"";
            }

            var query = queryParams.Count == 0
                ? "null"
                : "Query(" + string.Join(", ", queryParams.Select(p => $"(\"{p.Name}\", Format({Camel(p.Name)}))")) + ")";

            sb.AppendLine($"    /// <summary>{System.Security.SecurityElement.Escape(op.Summary)} (<c>GET {op.Path}</c>).</summary>");
            if (op.Binary)
            {
                sb.AppendLine($"    public Task<LibrusBinary> {methodName}({string.Join(", ", signature)}) =>");
                sb.AppendLine($"        GetBinaryAsync({pathExpr}, {query}, cancellationToken);");
            }
            else
            {
                sb.AppendLine($"    public Task<{op.ResponseType}> {methodName}({string.Join(", ", signature)}) =>");
                sb.AppendLine($"        GetJsonAsync({pathExpr}, {query}, {prefix}JsonContext.Default.{op.ResponseType}, cancellationToken);");
            }

            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Camel(string name)
    {
        var p = Pascal(name);
        var camel = char.ToLowerInvariant(p[0]) + p[1..];
        return camel is "class" or "event" ? "@" + camel : camel;
    }

    private StringBuilder Header()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine($"//   Generated by tools/Librus.OpenApiGen from {doc["info"]?["title"]?.GetValue<string>()} v{doc["info"]?["version"]?.GetValue<string>()}.");
        sb.AppendLine("//   Do not edit by hand; re-run the generator instead (see README).");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("#nullable enable");
        return sb;
    }

    private sealed record ClassModel(string Name, string Fingerprint, string SchemaKey)
    {
        public List<PropertyModel> Properties { get; } = [];
    }

    private sealed record PropertyModel(string JsonName, string Type, bool Nullable, bool Required);

    private sealed record Param(string Name, string In, string Type);

    private sealed record Operation(string Id, string Path, string Summary, List<Param> Parameters, string? ResponseType, bool Binary);
}
