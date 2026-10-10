# OpenAPI documents

Copied verbatim from [andrewkoltsov/librus-sdk](https://github.com/andrewkoltsov/librus-sdk)
(`openapi/`, v0.7.1, MIT License, Copyright (c) 2026 Andrey Koltsov):

| File | Surface |
| --- | --- |
| `api_v3_openapi.json` | child-scoped GETs on `https://api.librus.pl/3.0` (portal bearer) |
| `wiadomosci_librus_pl_api_openapi.json` | inbox reads on `https://wiadomosci.librus.pl/api` (cookie handoff) |

They are the source of truth for the generated DTOs in `src/Librus.Api/Generated`.
To refresh: copy newer versions here and run `./tools/generate.sh`.

```
MIT License

Copyright (c) 2026 Andrey Koltsov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
