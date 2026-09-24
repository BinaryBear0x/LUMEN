# LUMEN

[English](README.md) | [Türkçe](README.tr.md)

![Animated LUMEN app icon](./HuePC.App/Assets/lumen-icon-animated.gif)

LUMEN controls Philips Hue Bluetooth bulbs directly from a Windows 11 PC over BLE. No Hue Bridge is required.

I made this project because the current Hue mobile app did not work well for my needs, and there was no app for controlling the bulb from a PC. LUMEN brings desktop control together with light profiles, timers, and automations based on PC activity and state.

LUMEN is not an official Philips Hue app. BLE control has been tested with the bulb listed below; other models and firmware versions need to be verified separately.

## Features

- Discover bulbs, connect, check Windows pairing, and remember the last-used bulb.
- Control power, brightness, white color temperature, color, and identify the bulb.
- Run effects on the bulb and adjust their speed; use built-in or custom color profiles.
- **Beat**, **Calm**, and **Synesthesia** modes that react to the PC's audio output.
- Weekly timers, Pomodoro, a weekly alarm view, and a wake-up ramp that gradually raises brightness.
- Add rules to Windows notifications by app and color.
- Mirror screen colors to the bulb; adjust the light or show alerts based on circadian rhythm, weather, and earthquake reports.
- Use microphone or camera activity, and signal battery, charging, and network changes through the light.
- Connection diagnostics, local logs, and background operation from the system tray.

Weather, map, and earthquake data require internet access. BLE bulb control is local. Notifications, microphone, and camera features depend on Windows permissions.

## Requirements

- Windows 11 x64, build 22000 or later
- A Bluetooth LE adapter
- A Philips Hue bulb that supports Bluetooth control

## Getting started

1. Turn on the bulb and enable Bluetooth on the PC.
2. If needed, pair the bulb in **Settings → Bluetooth & devices** in Windows. The control characteristics require an encrypted connection. On the tested bulb, its pairing window stays open for about 30 minutes after power-on.
3. In the app, select **Find bulbs**, choose a bulb from the list, and connect.
4. Use the **Control**, **Effects**, **Profiles**, **Music**, **Notifications**, **Environment**, or **Timer** pages.

The first Windows BLE discovery can take a while. If the device is not cached, connecting may take about 30 seconds. The target bulb may allow only one control connection; connecting from Windows can disconnect the phone's Hue app. Closing the window minimizes LUMEN to the system tray. The PC must be on and LUMEN running for alarms and automations to work.

## Run from source

.NET 10 SDK and Windows 11 are required.

```powershell
dotnet restore HuePC.sln
dotnet build HuePC.sln --configuration Release
dotnet run --project HuePC.App\HuePC.App.csproj
```

The validation project is a console runner and does not use MSTest or NUnit:

```powershell
dotnet run --project HuePC.Tests\HuePC.Tests.csproj --configuration Release
```

### GATT inspection tool

`tools/HuePC.HardwareProbe` is a CLI, not a GUI. It inspects Hue/Signify candidates and lists GATT services, characteristics, and readable values. It does not scan unbranded devices that fail the Hue filter. It does not write to characteristics or subscribe to notifications, but it does establish a real BLE connection.

```powershell
dotnet run --project tools\HuePC.HardwareProbe\HuePC.HardwareProbe.csproj
dotnet run --project tools\HuePC.HardwareProbe\HuePC.HardwareProbe.csproj -- --adapter
dotnet run --project tools\HuePC.HardwareProbe\HuePC.HardwareProbe.csproj -- --device "AA:BB:CC:DD:EE:FF"
```

## BLE protocol

The custom GATT mapping below is not a Philips-published Windows API. It describes observed behavior on an LCA016 with firmware 1.126.9; it is not guaranteed for other models or firmware. Control characteristics require a paired/encrypted connection. Send commands using **Write With Response**.

Control service: `932C32BD-0000-47A2-835A-A8D455B859DD`

| Characteristic | Data | Function |
|---|---|---|
| `932C32BD-0002-47A2-835A-A8D455B859DD` | `00` off, `01` on | Power |
| `932C32BD-0003-47A2-835A-A8D455B859DD` | 1 byte, `01`–`FE` | Brightness (1–254) |
| `932C32BD-0004-47A2-835A-A8D455B859DD` | `uint16 LE`, mired | White color temperature; tested range 154–455 mired |
| `932C32BD-0005-47A2-835A-A8D455B859DD` | `x uint16 LE` + `y uint16 LE` | CIE xy color coordinates |
| `932C32BD-0006-47A2-835A-A8D455B859DD` | `01` | Identify the bulb by briefly flashing it |
| `932C32BD-0007-47A2-835A-A8D455B859DD` | TLV fields | Combined state, color/brightness, and effects; state notifications arrive on this characteristic |
| `932C32BD-1005-47A2-835A-A8D455B859DD` | TLV fields | Power-on behavior to apply when power is restored |

The `0007` payload consists of consecutive `type, length, value` fields. Notifications may contain only changed fields; do not treat a missing field as zero.

| TLV type | Length | Value |
|---|---:|---|
| `01` | 1 | Power: `00` off, `01` on |
| `02` | 1 | Brightness: `01`–`FE` |
| `03` | 2 | Color temperature, `uint16 LE` mired |
| `04` | 4 | `x uint16 LE`, followed by `y uint16 LE` |
| `06` | 1 | Effect ID; `00` stops the effect |
| `08` | 1 | Effect speed: `01`–`FE` |

Example: `02 01 80 04 04 23 4C 56 7A` sets brightness to `0x80` (128) and xy color to `x=0x4C23`, `y=0x7A56`.

Effect IDs: `01` Candle, `02` Fireplace, `03` Prism, `0A` Glisten, `0B` Opal, `0C` Sparkle, `0E` Underwater, `0F` Cosmos, `10` Sunbeam, `11` Magic. Send an effect and its speed in the same write using `06 01 <effect> 08 01 <speed>`.

Tested power-on payloads for `1005`:

| Behavior | Hex |
|---|---|
| Always turn on | `01 01 01 02 01 FE 03 02 6E 01 04 04 FF FF FF FF` |
| Use last color and brightness | `01 01 01 02 01 FF 03 02 FF FF 04 04 FF FF FF FF` |
| Restore previous state | `01 01 FF 02 01 FF 03 02 FF FF 04 04 FF FF FF FF` |

In these payloads, `FF` tells the bulb to use its previous value for that field.

### Writing your own BLE app

HuePC does not expose a callable REST, socket, or plugin API. Your client must connect directly to the bulb as a BLE GATT central:

1. Use the Hue advertised name or the `0000FE0F-0000-1000-8000-00805F9B34FB` Signify service UUID as a candidate filter. Company ID `0xFE0F` alone does not identify a model.
2. Pair the bulb through Windows Bluetooth settings.
3. Discover the control service and the characteristics you need.
4. Send commands with write-with-response. For state tracking, subscribe to notifications on characteristic `0007` and handle partial TLV updates.
5. Verify behavior on real devices when using other models and firmware versions.

### Device information UUIDs

| Service | Characteristic | Function |
|---|---|---|
| `0000FE0F-0000-1000-8000-00805F9B34FB` | `97FE6561-0001-4F62-86E9-B71EE2DA3D22` | Zigbee address; 8 bytes |
| Same service | `97FE6561-0003-4F62-86E9-B71EE2DA3D22` | Bulb name; UTF-8 |
| `0000180A-0000-1000-8000-00805F9B34FB` | `00002A29-0000-1000-8000-00805F9B34FB` | Manufacturer name |
| Same service | `00002A24-0000-1000-8000-00805F9B34FB` | Model number |
| Same service | `00002A28-0000-1000-8000-00805F9B34FB` | Firmware version |

## Problems solved during development

| Problem | Solution |
|---|---|
| Windows could not open an unpaired BLE address that was not in its cache. The old lookup timed out without finding the device. | Try a fast address connection first. If it fails, search for unpaired Windows BLE endpoints for up to 35 seconds, then retry by address. The first connection may take longer. |
| The custom Hue GATT protocol was unclear; names such as `Write` and `Notify` did not explain what the commands did. | Inspect the GATT layout with the separate HardwareProbe tool. Verify UUIDs and payloads on a real bulb through reads, write-with-response, and notifications. |
| Some writes appeared successful on an unpaired connection, but the bulb did not apply the command. | Control characteristics were found to require bonding. Require Windows pairing and write-with-response. |
| Windows could raise a `NotificationChanged` error in an unpackaged desktop app. | Poll the notification list every two seconds. Do not count notifications that already exist when the app starts as new. |
| WASAPI returned `WAVE_FORMAT_EXTENSIBLE` audio on some devices, which the FFT did not expect. | Convert the audio to standard PCM before analysis. |
| Screen, music, and profile features could send BLE commands too often or at the same time. | Reduce screen sampling frequency, debounce UI values, and combine color/brightness in one TLV write. |
| AFAD and EMSC use different time formats, and the same earthquake could trigger duplicate alerts. | Convert times from UTC to local time. Deduplicate records by proximity in time, location, and magnitude. |

## Validation status and limitations

- Direct GATT control has been verified on **LCA016, firmware 1.126.9**. Do not assume compatibility with other Hue models or firmware.
- Earthquake alerts are not predictions; LUMEN checks AFAD and EMSC records after they are published.
- Live hardware testing of battery/charging/network change triggers, a wake-up ramp during a real alarm, and an end-to-end alert for a new earthquake report has not been completed.
- Weather, map, and earthquake data come from online services.
- LUMEN logs may contain BLE addresses, advertisement data, and GATT values. Review and remove these fields before sharing logs.

## Local data

- App settings: `%LocalAppData%\HuePC\settings.json`
- Bulb names: `%LocalAppData%\HuePC\device-aliases.json`
- Timers: `%LocalAppData%\HuePC\schedules.json`
- Remembered bulb: `%LocalAppData%\HuePC\remembered-device.json`
- Logs: `%LocalAppData%\HuePC\Logs`

## Project structure

```text
HuePC.App             WPF UI and user workflows
HuePC.Core            Models, protocol, and business rules
HuePC.Bluetooth       Windows BLE discovery, connection, and GATT
HuePC.Audio           WASAPI loopback audio capture
HuePC.Notifications   Windows notification monitoring
HuePC.System          Screen, weather, earthquake, and system events
HuePC.Infrastructure  JSON storage and local logging
HuePC.Tests           Core and storage checks
tools/                Hue GATT inspection CLI
```

## Contributing and license

For code, documentation, verification on other models/firmware, and bug reports, see the [contribution guide](CONTRIBUTING.md). When reporting a bug, include the Windows version, Bluetooth adapter, bulb model, and firmware version. Do not leave device addresses in logs or GATT output.

The project is released under the [MIT License](LICENSE). Third-party packages and images are subject to their own license terms. Philips Hue and Signify are trademarks of their respective owners.
