# Third-party notices

- Aholo Viewer / EGS, Copyright © 2026 MANYCORE, INC., MIT. Original notice: `Aholo-MIT.txt`. Native tool plus Aholo/EGS schema and rendering conventions are used under that license.
- `Editor/Converter/Native~/splat-transform.exe`: official Windows x64 native 2.1.0 from `@manycore/aholo-splat-transform-win32-x64-msvc`. SHA256: `5F8450254E3D3C9700C414DB2D3328999D421DC47B64B639B7FBF5AA8B733CEE`. Full bundled dependency notices: `Aholo-Native-Dependencies.txt`.
- Native source: https://github.com/manycoretech/aholo-viewer, studied revision `fab5d30d0dcb8f9f7f6cc80914a68aaae5a73d17`. EGS submodule `78aa5d8f31fff80079e979e10b255cbbbb2415b4`.
- Newtonsoft JSON and Unity URP are resolved separately through UPM and retain their respective package licenses.

The existing Aras Gaussian Splatting package in the host project is not bundled into, or required by, this package. The new runtime uses its own structured buffers and shaders.
