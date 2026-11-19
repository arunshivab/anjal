# PageGeometry

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §3.1, §7.4): between a page as the PDF holds it (user space: bottom-left origin, y up, unrotated) and the page as the person sees it (its crop box, turned by its /Rotate, top-left origin, y down), in points. The library draws a page turned and cropped (ApplyPageRotation); its text, search boxes and redaction boxes are in user space - so every position shown over a page picture goes through here.

## Members

- **#ctor** *(method)* - Takes the box the renderers show (chuvadi-pdf 3.34.0 GetDisplayBox: the CropBox intersected with the MediaBox, normalised) and the page's turn, taken at its nearest lower multiple of 90 as the renderers take it - so the positions shown over a page match the library's own display space, which the editor writes through.
- **#ctor** *(method)* - Takes a crop box and a turn directly (for tests).
- **ToShown** *(method)* - A point in user space as shown (from the top-left).
- **ToShownAngle** *(method)* - A direction in user space (degrees, counter-clockwise) as a turn on the screen (degrees, clockwise).
- **ToShownBox** *(method)* - A user-space rectangle (left, bottom, width, height) as a box shown from the top-left.
- **ToUser** *(method)* - A point as shown (from the top-left) in user space - the way back (§7.4).
- **Rotate** *(property)* - The page's turn, clockwise.
- **ShownHeight** *(property)* - The height as shown.
- **ShownWidth** *(property)* - The width as shown.
