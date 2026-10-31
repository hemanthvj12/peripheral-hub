# Controller drawings

The controller pictures: `DeviceArt.Silver` (`DeviceArt.cs`) reads these SVGs at runtime and fills them in the app's
"Silver + brand" style (silver body, light touchpad, dark sticks, purple-to-blue buttons and D-pad, the lines on top).
Other devices are drawn in `../GenericPictures.xaml`.

- `dualsense.svg`, `dualsense-edge.svg`, `dualshock-4.svg`, `xbox-series.svg`: the bold SVGs from the
  **Gamepad Line Art Pack** by gidedin (https://gidedin.itch.io/gamepad-line-art-pack), CC0 1.0
  (`GAMEPAD-PACK-LICENSE.txt`; no credit required, recorded here anyway). Unmodified. The pack also has Xbox One,
  Xbox Elite 2, Switch Pro and Joy-Con drawings if those controllers are ever supported.

Check the result with `PeripheralHub.exe --write-art <file.png>`.
