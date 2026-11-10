# Third-party notices (Phase 0 spikes)

## smourier/VCamSample

- Source: https://github.com/smourier/VCamSample (cloned 2026-11-10, default branch)
- Used in: `spikes/vcam/src/` — `Activator.*`, `MediaSource.*`, `MediaStream.*`, `dllmain.cpp`,
  `Tools.*`, `MFTools.*`, `EnumNames.*`, `WinTrace.*`, `Undocumented.h`, `framework.h`, `pch.h`,
  `RaaiCamVCam.def` (ported and modified; each file carries an attribution header).
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

- Source: https://github.com/microsoft/wil, tag `v1.0.260126.7`
- Not vendored: fetched at configure time by CMake `FetchContent` into `spikes/vcam/build/_deps`.
- License: MIT, Copyright (c) Microsoft Corporation.
