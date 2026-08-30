# Third-party notices

Sling is built on the components below. Each is used unmodified, under its own
licence, and each of those licences requires that its copyright notice and
permission notice travel with any copy of the software.

This file is that notice. **It ships inside the release archive** - it is not
only a repository courtesy, it is a condition of using these components at all.

Sling's own licence (`LICENSE`) does not apply to any of them, and nothing in it
restricts your rights under theirs.

---

## AvalonEdit 6.3.1.120

The text editor control behind both panes: the document model, the text view,
folding, and the syntax-highlighting engine.

- Project: <https://github.com/icsharpcode/AvalonEdit>
- Licence: MIT

```
MIT License

Copyright (c) AvalonEdit Contributors

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

---

## WPF UI 4.3.0

The Fluent window shell: the title bar, Mica backdrop, theming and controls.

- Project: <https://github.com/lepoco/wpfui>
- Licence: MIT

```
MIT License

Copyright (c) 2021-2025 Leszek Pomianowski and WPF UI Contributors. https://lepo.co/

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

---

## .NET 10 runtime and WPF

Sling ships self-contained, so the .NET runtime and the WPF libraries are
included in the release archive.

- Project: <https://github.com/dotnet/runtime>, <https://github.com/dotnet/wpf>
- Licence: MIT, Copyright (c) .NET Foundation and Contributors

The full text is the same MIT licence reproduced above, and is distributed with
the .NET runtime files in the archive.

---

## Etch.Core 1.0.1

The transform engine behind the response pane: format detection, the transform
catalogue, palette ranking, and the text utilities under them. `Etch.Core.dll`
is redistributed inside every Sling release.

Not third-party in the usual sense - it is the same author's code, from the
[Etch](https://github.com/HendrikVrey/Etch) project - but it is a **separately
licensed component**, so a reader of this file is entitled to know it is in the
binary and on what terms.

- Project: <https://github.com/HendrikVrey/Etch>
- Licence: **Etch Source-Available Licence v1.0** - not an open-source licence,
  and not the MIT terms every other redistributed component here carries. The
  full text ships inside the package and is at
  <https://github.com/HendrikVrey/Etch/blob/master/LICENSE>.
- Consumed from a private feed, not from nuget.org. `docs/etch-core-package.md`
  explains why and how to restore it.

`Etch.Core` itself has no package dependencies; Etch's own third-party
components are listed in that project's `THIRD-PARTY-NOTICES.md`.

---

## Not redistributed

These are used to build and test Sling. They are not part of any release and
are listed for completeness rather than obligation.

| Component | Licence | Used for |
|---|---|---|
| [xUnit.net v3](https://github.com/xunit/xunit) 3.2.2 | Apache-2.0 | test framework |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) 18.5.1 | MIT | test host |
| [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) 3.1.5 | Apache-2.0 | test adapter |

