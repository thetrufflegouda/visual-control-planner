# Using Visual Control Planner

## Start a plan

1. Choose a game, output, and profile. Use **Game files…** if VCP needs an install location. The profile picker opens in the game's bindings folder where available.
2. Open **Device Library**, select a controller, and choose **Add to plan**. You can add multiple copies of the same model. Choose the matching grip, base, or auxiliary panels when prompted. Use **Connect** on the device card to pair the layout with its Windows controller.
3. Select a control in the hardware viewer. Drag the background sideways to change angles. Use **Learn this control** to capture one input, **Input Configurator** to step through the device and hardware layer, or **…** to enter an input manually. During a sweep, **Back** and **Skip** move between controls.
4. Choose a game action and click **Assign**. **Unassign** removes a binding from the plan. Use **Preview / Export** to review and apply changes to the selected files. Direct exports require the edited controllers to be connected.

**Save plan** writes a `.vbind.json` file with local paths and controller pairings. VCP resumes your last working plan at startup, including unsaved edits. **New plan** starts an empty plan.

Use **Share plan** to make a copy for someone else. It keeps your planned assignments, learned inputs, custom action names, and vJoy routes. It omits local paths, controller pairings, and changes that remove bindings from your existing profiles. The recipient can connect their controllers and choose their own profiles. Bindings that exist only in a game profile and custom hardware layouts saved in your device library live in separate files. Review any custom names you entered before sharing.

When opening a plan that contains network-share or Windows device paths, VCP asks whether to open a copy without saved paths or allow Windows to access those paths.

## Gremlin and vJoy

- Choose **Joystick Gremlin** as the output. Use **Profile** to open a Gremlin XML or **New…** to create one. **Auto-map inputs** maps buttons and available axes in device-strip order, then by hardware layer and input number. It preserves existing axis routes and reports any inputs left over.
- Use the vJoy selectors for one control, or open **Patch Bay** to wire inputs to outputs. Physical inputs use learned or reference numbers for sorting. Click a wire or input to select it, hold **Shift** to select a range or **Ctrl** to select individual items, and use **Delete** or **Remove route** to disconnect.
- When a **Game Binding Profile** selector is available, choose the native profile independently of the Gremlin XML. **Preview / Export** can update the vJoy game bindings and Gremlin routes together for Star Citizen, War Thunder, and SWG Space. Star Citizen profiles need a vJoy slot, War Thunder profiles need an identifiable vJoy device, and SWG needs the character's Space keymap.
- To reserve an input as a modifier, choose **Hold to shift** or **Toggle shift**. **Gremlin Shift** shows the software layer and dims routes inherited from its base mode. **Hardware Shift** shows the layer reported by the controller. Give the software Shift control a separate output to override an inherited route.
- **Custom to vJoy** accepts action lists entered in VCP or imported from text, XML, JSON, and Unity `.inputactions` files. The actions are saved with the plan. Load exported XML in Joystick Gremlin 16.2. Auto-mapping uses the configured vJoy driver.

## Diagrams and custom hardware

**Binding Diagram** stays open and updates as you edit. The hardware views start expanded. Hats use directional callouts and layers have distinct colors. Select a controller or search for an action. **Unplaced inputs** shows saved inputs whose location VCP cannot identify.

Use **Custom controllers** to lay out a device on a fine grid. Drag a panel or part edge to resize it, use **Rotate 90°** for adjustable parts, and copy and paste to retain size and rotation. Press **Delete** or **Backspace** to remove the selected part. Saved layouts appear in the local device library.

**My Devices** is shared between plans for the current Windows user. The library includes common PlayStation and Xbox controllers. Use **Learn this control** for driver-dependent reports. The standard Microsoft XUSB DirectInput driver reports Xbox triggers on one shared axis.

## Game profiles

| Game | Profile notes |
| --- | --- |
| DCS World | VCP scans installed aircraft, including mods, and finds the active Saved Games `.diff.lua` for each paired controller. Choose a different file per device or create one. VCP resolves commands from the install and saved profiles with DCS closed. DCS’s **Make HTML** report can supply identifiers for additional commands. |
| Elite Dangerous | Select the `.binds` preset that contains the controller you want to edit. |
| IL-2 Great Battles | VCP can update the Global `.actions` and `current.map` pair or an aircraft profile already assigned in IL-2. Create and assign new aircraft profiles in IL-2 first. |
| IL-2 Cliffs of Dover Blitz | VCP edits `confUser.ini` or a saved control preset. Confirm the in-game joystick slot for presets that use numbered slots. |
| Microsoft Flight Simulator | Direct edits use compatible controller XML profiles. Microsoft Store and Xbox profiles require Microsoft’s signed `xgamesaveutil.exe` from the Windows PC Game Development Kit. VCP backs up the save, updates the selected containers, and verifies the result with the simulator closed. |
| Star Citizen | Select an exported controls XML. **Default**, **Tap**, **Double Tap**, and **Hold** are available for supported button actions. |
| Star Wars: Squadrons | Select `ProfileOptions_profile_synced`. Register the physical device in the game first so the file contains the HOTAS slot. |
| SWG Legends and Restoration | VCP writes flight controls to the character’s `_space.inp`. Select the corresponding joystick in the SWG launcher. |
| War Thunder | Use an exported `.blk` or the active `machine.blk` if it matches the game’s `last` copy. The preset needs a recognized device map. **Tap**, **Double Tap**, and **Hold** are available for button assignments. |

## Portable folder

Open `VisualControlPlanner.exe` and keep `Support` beside it. The Windows package includes the .NET Desktop Runtime.

## Privacy

VCP has no telemetry or analytics. It stores your working plan, device library preferences, and custom layouts under `%LOCALAPPDATA%\VisualControlsBinder`. Native game profiles, Gremlin profiles, and recovery backups can contain account, character, or controller details. Keep those files private when sharing a plan.

Microsoft Store and Xbox Flight Simulator exports invoke the installed Microsoft `xgamesaveutil.exe` tool when you choose that export. VCP stores the recovery copies under `%LOCALAPPDATA%\VisualControlsBinder\Backups`.
