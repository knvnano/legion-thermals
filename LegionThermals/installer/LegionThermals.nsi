Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "1.3.0"
!endif
!define APP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\LegionThermals"
Name "Legion Thermals"
OutFile "..\dist\LegionThermals-Setup-${APP_VERSION}-win-x64.exe"
InstallDir "$PROGRAMFILES64\Legion Thermals"
InstallDirRegKey HKLM "${APP_KEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 32
SetDatablockOptimize on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName" "Legion Thermals"
VIAddVersionKey /LANG=1049 "FileDescription" "Установщик Legion Thermals для Windows x64"
VIAddVersionKey /LANG=1049 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Copyright © 2026 Авторы Legion Thermals"

!define MUI_ICON "..\Assets\app.ico"
!define MUI_UNICON "..\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!define MUI_LICENSEPAGE_CHECKBOX
!insertmacro MUI_PAGE_LICENSE "LICENSE.generated.rtf"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApplication
!define MUI_FINISHPAGE_RUN_TEXT "$(RunText)"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "$(UninstallText)"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"
LangString WelcomeText ${LANG_RUSSIAN} "Установка Legion Thermals ${APP_VERSION} для Windows x64.$\r$\n$\r$\nМониторинг температур CPU/GPU и управление вентиляторами Lenovo.$\r$\n.NET включён в пакет. Закройте старую копию через меню трея → Выход."
LangString WelcomeText ${LANG_ENGLISH} "Install Legion Thermals ${APP_VERSION} for Windows x64.$\r$\n$\r$\nCPU/GPU monitoring and Lenovo fan control.$\r$\n.NET is included. Close the old copy using the tray menu → Exit before updating."
LangString LicenseTop ${LANG_RUSSIAN} "Программа с открытым кодом под лицензией MIT."
LangString LicenseTop ${LANG_ENGLISH} "Open-source software licensed under the MIT License."
LangString RunText ${LANG_RUSSIAN} "Запустить Legion Thermals с правами администратора"
LangString RunText ${LANG_ENGLISH} "Launch Legion Thermals as administrator"
LangString UninstallText ${LANG_RUSSIAN} "Сначала завершите программу через меню трея → Выход.$\r$\nЛичные настройки и посторонние файлы сохранятся."
LangString UninstallText ${LANG_ENGLISH} "First close the app using the tray menu → Exit.$\r$\nPersonal settings and unrelated files will be preserved."
LangString Need64 ${LANG_RUSSIAN} "Требуется 64-разрядная Windows."
LangString Need64 ${LANG_ENGLISH} "64-bit Windows is required."
LangString Need10 ${LANG_RUSSIAN} "Требуется Windows 10 или новее."
LangString Need10 ${LANG_ENGLISH} "Windows 10 or later is required."
LangString AppTitle ${LANG_RUSSIAN} "Программа (обязательно)"
LangString AppTitle ${LANG_ENGLISH} "Application (required)"
LangString DesktopTitle ${LANG_RUSSIAN} "Ярлык на рабочем столе"
LangString DesktopTitle ${LANG_ENGLISH} "Desktop shortcut"
LangString AppDescription ${LANG_RUSSIAN} "Программа, встроенный .NET, меню «Пуск», лицензии и удаление."
LangString AppDescription ${LANG_ENGLISH} "Application, bundled .NET, Start Menu shortcuts, licenses and uninstaller."
LangString DesktopDescription ${LANG_RUSSIAN} "Создать ярлык с запросом прав администратора."
LangString DesktopDescription ${LANG_ENGLISH} "Create a desktop shortcut requesting administrator access."
LangString ShortcutError ${LANG_RUSSIAN} "Не удалось настроить UAC для ярлыка: $0 (HRESULT $5)."
LangString ShortcutError ${LANG_ENGLISH} "Could not configure shortcut elevation: $0 (HRESULT $5)."
LangString ShortcutWarning ${LANG_RUSSIAN} "Не удалось настроить ярлык. Запускайте программу от администратора."
LangString ShortcutWarning ${LANG_ENGLISH} "Could not configure the shortcut. Run the app as administrator."

Function .onInit
  !insertmacro MUI_LANGDLL_DISPLAY
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "$(Need64)"
    SetErrorLevel 1
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "$(Need10)"
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext all
  IfSilent check_acceptance done_init
check_acceptance:
  ${GetParameters} $0
  ${GetOptions} $0 "/ACCEPTEULA=" $1
  ${If} $1 != "1"
    SetErrorLevel 2
    Abort
  ${EndIf}
done_init:
FunctionEnd

Section "$(AppTitle)" AppSection
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "payload\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "${APP_KEY}" "DisplayName" "Legion Thermals"
  WriteRegStr HKLM "${APP_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${APP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${APP_KEY}" "DisplayIcon" "$INSTDIR\LegionThermals.exe,0"
  WriteRegStr HKLM "${APP_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKLM "${APP_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKLM "${APP_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${APP_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKLM "${APP_KEY}" "EstimatedSize" $0
  CreateDirectory "$SMPROGRAMS\Legion Thermals"
  CreateShortCut "$SMPROGRAMS\Legion Thermals\Legion Thermals.lnk" "$INSTDIR\LegionThermals.exe"
  Push "$SMPROGRAMS\Legion Thermals\Legion Thermals.lnk"
  Call ElevateShortcut
  CreateShortCut "$SMPROGRAMS\Legion Thermals\Uninstall Legion Thermals.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortCut "$SMPROGRAMS\Legion Thermals\User Guide.lnk" "$INSTDIR\INSTALL.en.txt"
SectionEnd

Section /o "$(DesktopTitle)" DesktopSection
  CreateShortCut "$DESKTOP\Legion Thermals.lnk" "$INSTDIR\LegionThermals.exe"
  Push "$DESKTOP\Legion Thermals.lnk"
  Call ElevateShortcut
  WriteRegDWORD HKLM "${APP_KEY}" "DesktopShortcut" 1
SectionEnd

Function LaunchApplication
  ExecShell "runas" "$INSTDIR\LegionThermals.exe" "--settings"
FunctionEnd

# Set SLDF_RUNAS_USER through the system Shell Link COM interfaces.
# This affects only shortcuts created above, not the application manifest or startup.
Function ElevateShortcut
  Exch $0
  Push $1
  Push $2
  Push $3
  Push $4
  Push $5
  System::Call 'ole32::CoCreateInstance(g "{00021401-0000-0000-C000-000000000046}", p 0, i 1, g "{000214F9-0000-0000-C000-000000000046}", *p .r1) i .r5'
  ${If} $5 = 0
    System::Call '$1->0(g "{0000010b-0000-0000-c000-000000000046}", *p .r2) i .r5'
    ${If} $5 = 0
      System::Call '$2->5(w r0, i 0) i .r5'
      ${If} $5 = 0
        System::Call '$1->0(g "{45e2b4ae-b1c3-11d0-b92f-00a0c90312e1}", *p .r3) i .r5'
        ${If} $5 = 0
          System::Call '$3->6(*i .r4) i .r5'
          ${If} $5 = 0
            IntOp $4 $4 | 0x2000
            System::Call '$3->7(i r4) i .r5'
            ${If} $5 = 0
              System::Call '$2->6(w r0, i 1) i .r5'
            ${EndIf}
          ${EndIf}
          System::Call '$3->2()'
        ${EndIf}
      ${EndIf}
      System::Call '$2->2()'
    ${EndIf}
    System::Call '$1->2()'
  ${EndIf}
  ${If} $5 != 0
    DetailPrint "$(ShortcutError)"
    MessageBox MB_OK|MB_ICONEXCLAMATION "$(ShortcutWarning)"
  ${EndIf}
  Pop $5
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
FunctionEnd

Section "Uninstall"
  # Only remove this user's app-owned task if its action targets this copy.
  ExecWait '$\"$INSTDIR\LegionThermals.exe$\" --remove-startup-for-this-copy' $0
  # Remove the Run value only when it points to this installation.
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "LegionThermals"
  ${If} $0 == '$\"$INSTDIR\LegionThermals.exe$\"'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "LegionThermals"
  ${EndIf}
  ReadRegDWORD $0 HKLM "${APP_KEY}" "DesktopShortcut"
  ${If} $0 == 1
    Delete "$DESKTOP\Legion Thermals.lnk"
  ${EndIf}
  Delete "$SMPROGRAMS\Legion Thermals\Legion Thermals.lnk"
  Delete "$SMPROGRAMS\Legion Thermals\Uninstall Legion Thermals.lnk"
  Delete "$SMPROGRAMS\Legion Thermals\User Guide.lnk"
  Delete "$SMPROGRAMS\Legion Thermals\Удалить Legion Thermals.lnk"
  Delete "$SMPROGRAMS\Legion Thermals\Инструкция.lnk"
  RMDir "$SMPROGRAMS\Legion Thermals"
  !include "uninstall-files.nsh"
  Delete /REBOOTOK "$INSTDIR\Uninstall.exe"
  ReadRegStr $0 HKLM "${APP_KEY}" "InstallLocation"
  ${If} $0 == $INSTDIR
    DeleteRegKey HKLM "${APP_KEY}"
  ${EndIf}
  RMDir "$INSTDIR"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${AppSection} "$(AppDescription)"
  !insertmacro MUI_DESCRIPTION_TEXT ${DesktopSection} "$(DesktopDescription)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END


