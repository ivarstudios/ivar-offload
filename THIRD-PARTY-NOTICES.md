# Third-party notices

IVAR Offload's own code is licensed under the GNU General Public License v3.0 ([LICENSE](LICENSE)). It uses one package
besides .NET itself: System.IO.Hashing (below). The published, self-contained programs (`IVAR Offload.exe`,
`ivar-offload.exe`) include:

## System.IO.Hashing

Copyright © .NET Foundation and Contributors. Used for the xxHash64 checksums in the ASC MHL manifests Backup writes.
Licensed under the MIT license: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
> rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
> persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
> Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
> WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
> COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
> OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## ASC MHL

The ASC MHL format (version 2) is a specification of the American Society of Cinematographers; IVAR Offload writes it
with its own code. The ASC's reference tool `ascmhl` (MIT license, <https://github.com/ascmitc/mhl>) is used by the
tests only and is not distributed.

## Microsoft .NET runtime and Windows Desktop runtime (WPF)

Copyright © .NET Foundation and Contributors / Microsoft Corporation.

The .NET runtime and Windows Desktop (WPF) binaries for Windows are distributed under the Microsoft .NET Library
License: <https://dotnet.microsoft.com/en-us/dotnet_library_license.htm>. They are not covered by the GPL. You may use
them only as part of the programs, not separately; Microsoft and its suppliers keep all rights in them.

The source code of .NET and WPF is available under the MIT license at <https://github.com/dotnet/runtime> and
<https://github.com/dotnet/wpf>, and their own third-party notices are at
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT> and
<https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT>.

## Fonts

The app uses the Segoe Fluent Icons / Segoe MDL2 Assets fonts that come with Windows. They are not included in the
programs.

## Test-only packages (not distributed)

xUnit, Microsoft.NET.Test.Sdk and coverlet are used by the test project only.

## Name and logo

The IVAR name and the IVAR Studios logo (the hexagon in the app's icon) are trademarks of IVAR Studios. They are not
licensed under the GPL (section 7(e) of the license).
