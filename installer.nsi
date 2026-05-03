; ============================================================================
;  Fileserver Drive Manager - NSIS Installer
;  IMPORTANT: This installer bundles a SELF-CONTAINED .NET 8 exe.
;  There is NO .NET runtime check, no download prompt, nothing.
;  The exe runs on a clean Windows 10/11 box without .NET installed.
; ============================================================================

!ifndef VERSION
  !define VERSION "0.0-test"
!endif

!ifndef SOURCE_EXE
  !define SOURCE_EXE "publish\FileserverDriveManager.exe"
!endif

!ifndef FILE_VERSION
  !define FILE_VERSION "0.0.0.0"
!endif

!define APP_NAME       "Fileserver Drive Manager"
!define APP_PUBLISHER  "Dyna Training"
!define APP_EXE        "FileserverDriveManager.exe"
!define APP_REGKEY     "Software\Microsoft\Windows\CurrentVersion\Uninstall\FileserverDriveManager"
!define APP_INSTALL_DIR "$PROGRAMFILES64\FileserverDriveManager"

Name "${APP_NAME} ${VERSION}"
OutFile "Drive Manager V${VERSION}.exe"
InstallDir "${APP_INSTALL_DIR}"
InstallDirRegKey HKLM "${APP_REGKEY}" "InstallLocation"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
Unicode true
ShowInstDetails show
ShowUninstDetails show

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define MUI_ABORTWARNING
!define MUI_ICON   "${NSISDIR}\Contrib\Graphics\Icons\modern-install.ico"
!define MUI_UNICON "${NSISDIR}\Contrib\Graphics\Icons\modern-uninstall.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN       "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT  "Launch ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey "ProductName"     "${APP_NAME}"
VIAddVersionKey "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey "FileVersion"     "${VERSION}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "FileDescription" "${APP_NAME} Installer"
VIAddVersionKey "LegalCopyright"  "(C) ${APP_PUBLISHER}"

Section "Install" SEC_INSTALL
  nsExec::Exec 'taskkill /F /IM "${APP_EXE}"'
  Sleep 500

  SetOutPath "$INSTDIR"
  SetOverwrite on

  File "/oname=${APP_EXE}" "${SOURCE_EXE}"
  File /nonfatal "LICENSE.txt"
  File /nonfatal "README.md"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut  "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut  "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut  "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr   HKLM "${APP_REGKEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr   HKLM "${APP_REGKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKLM "${APP_REGKEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKLM "${APP_REGKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${APP_REGKEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKLM "${APP_REGKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${APP_REGKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${APP_REGKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${APP_REGKEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${APP_REGKEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /F /IM "${APP_EXE}"'
  Sleep 500

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  DeleteRegKey HKLM "${APP_REGKEY}"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "FileserverDriveManager"
SectionEnd
