# Third-party notices (Windows components)

## smourier/VCamSample

- Source: https://github.com/smourier/VCamSample, commit `58b6d4b0e0c2c97fdffd66d23e7c8da6b89f570d` (the repository HEAD on 2026-11-10 when the Phase 0 spike in `spikes/vcam/src/` was taken; the exact commit ported was not recorded)
- Used in: `windows/RaaiCam.VirtualCam/src/` — `Activator.*`, `MediaSource.*`, `MediaStream.*`,
  `dllmain.cpp`, `Tools.*`, `MFTools.*`, `EnumNames.*`, `WinTrace.*`, `Undocumented.h`, `framework.h`,
  `pch.h`, `RaaiCamVCam.def` (ported and modified; each file carries an attribution header).
- License: MIT

```
MIT License

Copyright (c) 2024-2026 Simon Mourier

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

## microsoft/wil (Windows Implementation Libraries)

- Source: https://github.com/microsoft/wil, tag `v1.0.260126.7` = commit `cbf677fb0a942557d08fd129f4c106a76247b2ec`
- Not vendored: fetched at configure time by CMake `FetchContent` (pinned to that commit) into
  `windows/RaaiCam.VirtualCam/build/_deps`; header-only, compiled into `RaaiCamVCam.dll`.
- License: MIT, Copyright (c) Microsoft Corporation. All rights reserved.

```
    MIT License

    Copyright (c) Microsoft Corporation. All rights reserved.

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
    SOFTWARE
```

## doctest

- Source: https://github.com/doctest/doctest, tag `v2.4.11` = commit `ae7a13539fb71f270b87eb2e874fbac80bc8dda2`
- Not vendored: fetched at configure time by CMake `FetchContent` (pinned to that commit); used only by the
  `vcam_tests` unit-test executable (not shipped).
- License: MIT, Copyright (c) 2016-2023 Viktor Kirilov.

```
The MIT License (MIT)

Copyright (c) 2016-2023 Viktor Kirilov

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

## BouncyCastle.Cryptography

- Source: NuGet package `BouncyCastle.Cryptography` 2.6.2 (https://github.com/bcgit/bc-csharp), referenced (not vendored) by `RaaiCam.Protocol`.
- Used for: X25519 raw-key generation/agreement only (`Org.BouncyCastle.Math.EC.Rfc7748.X25519`); Windows CNG cannot import raw curve25519 private keys in .NET.
- License: MIT, Copyright (c) 2000-2025 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)

```
The MIT License (MIT)

Copyright (c) 2000-2025 The Legion of the Bouncy Castle Inc. (https://www.bouncycastle.org)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## QRCoder

- Source: NuGet package `QRCoder` 1.6.0 (https://github.com/codebude/QRCoder), referenced (not vendored) by `RaaiCam.Core`.
- Used for: rendering the pairing QR code as console text (`PairingQr.ToConsoleString`).
- License: MIT. Copyright (c) 2013-2024 Raffael Herrmann and contributors.
