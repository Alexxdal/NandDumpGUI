# NandDumpGUI

A Windows **WPF** application to **decode/correct NAND raw dumps** (data + OOB) using **BCH ECC**.  
It can export a **fixed data-only image** (page data) and optionally a **fixed RAW** (data + OOB) where corrected bytes and ECC can be rewritten.

The UI supports:
- **Primitive polynomial presets** + **manual polynomial input**
- **Transform modes**: `none`, `inv`, `bitrev`, `inv+bitrev`
- **Custom NAND layout** (page/oob/sector/chunk/ecc offset/len)
- **Quick Test**: tries multiple parameter combinations on a sample of pages and suggests the best set

> ⚠️ This project includes/links a BCH implementation derived from the Linux BCH code (GPL-2.0).  
> See **License** section below.

---

## Features

- ✅ Fix BCH errors in NAND dumps using a native BCH engine (DLL)
- ✅ Works on “adjacent RAW” dumps (PAGE + OOB repeated)
- ✅ Skips erased sectors (all `0xFF`) to speed up processing
- ✅ Writes **data-only** output (PAGE bytes per page)
- ✅ Optional: write **fixed RAW** output
- ✅ Optional: recompute and rewrite ECC bytes in OOB
- ✅ Quick Test: suggests the best params (polynomial/transform/oobdata, and optionally swapBits/t depending on build)

---

## Supported input format

- **RAW dump** must be consecutive pages:
  - `RAW_PAGE = PageSize + OobSize`
  - File size must be a multiple of `RAW_PAGE`

Example (common Broadcom-like layout):
- PageSize = 2048  
- OobSize = 64  
- SectorSize = 512  
- OobChunk = 16  
- EccOfs = 9  
- EccLen = 7  
- Steps = PageSize / SectorSize = 4  

---

## Build

### Requirements
- Visual Studio 2022 (or newer)
- Desktop development with C++ (MSVC toolset)
- .NET 8 SDK
- Target: **x64** (recommended)

### Solution layout (typical)
- `bchlib/` → native BCH library (C/C++ → `bchlib.dll`)
- `NandDumpGUI/` → WPF app
- `NandDumpGUI.Core/` → core logic (layout/fixer/quick test)
- `NandDumpGUI.Native/` → P/Invoke wrappers (`BchNative`, `BchContext`, etc.)

### Build steps
1. Build **bchlib** project (x64 Debug/Release) → produces `bchlib.dll`
2. Build **NandDumpGUI** (x64 Debug/Release)
3. Ensure `bchlib.dll` is copied into the WPF output folder:
   - `NandDumpGUI/bin/x64/(Debug|Release)/net8.0-windows/`
---

## Usage

1. Select **Input RAW** file (data+OOB interleaved)
2. Select **Output data-only** path
3. (Optional) enable **Save fixed RAW** and select output path
4. Configure:
   - Primitive polynomial (preset or manual)
   - `t` (error correction strength)
   - Transform mode
   - NAND layout parameters
   - OOB data bytes included in the BCH message (`oobdata`)
5. Click **Start**

### Quick Test (recommended)
Click **Quick Test** to automatically try multiple parameter combinations on a sample of pages and suggest the best settings.  
It’s useful when you don’t know the correct:
- primitive polynomial
- transform mode
- oobdata bytes
- (optionally) swapBits / candidate `t` values

---

## How to interpret results

The app reports:
- Total pages
- Checked sectors (non-erased)
- Uncorrectable sectors
- Pages modified
- Total corrected bitflips

### Warning: “Fix may have failed”
If the **uncorrectable ratio** is high, it usually means:
- wrong polynomial
- wrong transform
- wrong layout (chunk offset/len, step size, etc.)
- dump is heavily corrupted/noisy

**Suggestion:** run **Quick Test** and/or verify the NAND layout.

---

## Tips / Troubleshooting

### “bchlib.dll not found”
Make sure `bchlib.dll` is in the same folder as `NandDumpGUI.exe`:
- `.../bin/x64/Debug/net8.0-windows/` (or Release)

### Many uncorrectable sectors
Try:
- different transform (`inv`, `bitrev`, `inv+bitrev`)
- different primitive polynomial preset
- adjust `oobdata` (0..EccOfs)
- verify `OobChunk`, `EccOfs`, `EccLen`

### RAW size not multiple of RAW_PAGE
Your dump might not be “adjacent raw pages” (or has headers/metadata).  
You must provide a dump where each page is `PageSize + OobSize`.

---

## About

Created by **Alessandro D’Alterio**  
- Email: alexdal@live.it  
- Website: alexxdal.com

---

## License

This project uses a BCH implementation derived from Linux BCH code (GPL-2.0).  
If you distribute binaries that link this code, the resulting work is typically subject to **GPL-2.0** requirements.

If you need a permissive-license alternative, you would need a clean-room BCH implementation or a differently-licensed BCH library.
