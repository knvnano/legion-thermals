# Hardware support and firmware protocol

## Scope

Physically validated: Lenovo machine type **83DF**, Legion Pro5 16IRX9, **N0CN32WW** BIOS. The fan-write gate accepts83DF/N0CN. Other BIOS revisions within that family are not individually verified. Y9000 is a marketing name, not a reliable protocol identifier. Unknown models do not receive fan writes.

The source references below describe native interfaces. Their implementation code is not included in this project. No raw EC port I/O or additional driver is used.

## Lenovo WMI

Namespace: `ROOT\WMI`.

| Class / method | Input | Output / meaning |
| --- | --- | --- |
| LENOVO_OTHER_METHOD.GetFeatureValue | IDs0x05040000 /0x05050000 | value: current CPU/GPU°C |
| Same | IDs0x04030001 /0x04030002 | value: measured CPU/GPU RPM |
| Same | IDs0x04020000 | value: full-speed override flag,0/1 |
| LENOVO_OTHER_METHOD.SetFeatureValue | IDs0x04020000, value0/1 | Change only the full-speed override |
| LENOVO_GAMEZONE_DATA.GetSmartFanMode | None | Data: requested cooling/performance mode |
| LENOVO_GAMEZONE_DATA.SetSmartFanMode | Data |1/2/3 normal modes,255 Custom |
| LENOVO_FAN_METHOD.Fan_Get_Table | FanID byte, SensorID byte | Ten level values |
| LENOVO_FAN_METHOD.Fan_Set_Table | FanTable byte[64] | Curve shared by the fans |

`LENOVO_FAN_TABLE_DATA` provides per-fan RPM ladders. Current policy requires ten increasing positive levels. The legacy temperature-only fallback uses Gamezone GetCPUTemp/GetGPUTemp when available.

## Curve payload on the validated BIOS

Ten level bytes are at offsets6,8,…24 in a64-byte buffer; reserved bytes remain zero. Values are levels1–10, not PWM percentages. DSDT analysis of N0CN32WW showed WMAB writing F9F0…F9F9 and issuing NCMD(0x8C,0). The FSTM header byte is declared but not used on this BIOS. Do not generalize that behavior to another machine.

WMAE maps feature0x04020000 to EC.FNST. A latched full-speed flag can override an otherwise correctly saved curve in Custom. Early table-readback tests alone could not detect that physical failure. Current manual control clears the flag after a valid curve is written, checks it and later restores its original value. Hardware validation measures RPM through low→high→low requests and confirms original state restoration.

## Limitations

- Both temperatures must be available for application-controlled cooling.
- Percentages request a minimum curve floor, rounded up using both fan ladders.
- A different Lenovo utility may change the power mode or override settings.
- Normal exit restores state; a forced kill requires recovery at the next elevated launch.
- No universal support claim is made, and the app is not a thermal safety system.

## Primary references

- [Lenovo support for83DF](https://pcsupport.lenovo.com/lk/en/products/laptops-and-netbooks/legion-series/legion-pro-5-16irx9/83df/document-userguide)
- [Lenovo Legion Toolkit WMI Fan Method signatures](https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit/blob/master/LenovoLegionToolkit.Lib/System/Management/WMI.LenovoFanMethod.cs)
- [LenovoLegionLinux protocol research](https://github.com/johnfanv2/LenovoLegionLinux/blob/main/kernel_module/legion-laptop.c)
- [Windows Task Scheduler security contexts](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)
