# Local Setup Guide

## Branding (Logo & Icon)

The repository includes placeholder images for the logo and icon. To customize them for your organization:

### 1. Replace Logo
1. Prepare your company logo as a PNG image (recommended: 200x100 pixels)
2. Place it in the project root directory as `logo.png`
3. The logo appears in the top-right corner of the application

### 2. Replace Icon
1. Prepare your application icon as a PNG image (recommended: 32x32 pixels)
2. Place it in the project root directory as `icon.png`
3. The icon appears in the window title bar and system tray

### 3. Build and Run
` ` `powershell
cd C:\FileserverDriveManager
dotnet build --configuration Release
dotnet publish --configuration Release
.\bin\Release\net8.0-windows\win-x64\publish\FileserverDriveManager.exe
` ` `

## Files to Customize

- **logo.png** - Company/organization logo (placed in app directory)
- **icon.png** - Application window/tray icon (placed in app directory)

These files are in `.gitignore` to keep private branding out of the repository.

## Credentials

All credentials are encrypted and stored locally in:
` ` `
%APPDATA%\FileserverDriveManager-settings.json
` ` `

Never commit this file to git.

## First Run

1. Run the application
2. Enter fileserver credentials
3. Click 'Authenticate' 
4. Select shares and drive letters
5. Click 'Mount Drives'
6. App auto-enables startup and minimizes to tray when all drives connected

## Settings

Click **Settings** button to:
- Change fileserver IP (default: 192.168.1.26)
- Test connection to fileserver
- Replace logo.png
- Replace icon.png

All settings are saved to `%APPDATA%\FileserverDriveManager-settings.json`
