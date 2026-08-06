# Third party notices

## AiM XRK file format

`src/MyChron2Insta360.Core/XrkBinary.cs` reads AiM `.xrk` / `.xrz` logger files. The description of
that binary format comes from **libxrk**, an independent MIT-licensed implementation, and through it
from **TrackDataAnalysis** by Scott Smith:

- libxrk: https://github.com/m3rlin45/libxrk
- TrackDataAnalysis (incorporated into libxrk under the MIT License)

No code from AiM's own `MatLabXRK` library was decompiled, copied or derived. The reader here is an
independent implementation of the same file format, written against the published spec above, and it is
validated against real sessions: it reproduces AiM's library exactly (identical sample times, values
identical to float32 print precision) and matches RaceStudio's own CSV export to that export's rounding.
Run `MyChron2Insta360 <file>.xrk --verify-xrk` to reproduce that comparison.

```
MIT License

(For components copied from TrackDataAnalysis)
Copyright (c) 2024 Scott Smith

Copyright (c) 2025 Christopher Dewan

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

## Telemetry Overlay channel alias table

`docs/reference/telemetry-overlay-alias-table.txt` lists the channel names Telemetry Overlay
(goprotelemetryextractor.com) recognises. It is a list of names used for interoperability, extracted
from the application's own string constants, and is included so a column can be given a name the
application will bind to.

## Garmin FIT SDK

FIT files are written with the official `Garmin.FIT.Sdk` NuGet package, under Garmin's own licence.

## AiM XRK library (optional, not distributed)

`--verify-xrk` can cross-check the reader against AiM's `MatLabXRK` library if you have it. That library
is AiM's proprietary redistributable and is **not** shipped with this project; it is loaded at run time
only if you supply it yourself. Nothing in this project requires it.
