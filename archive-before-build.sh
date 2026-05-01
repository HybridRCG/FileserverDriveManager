#!/bin/bash

# Archive script for FileserverDriveManager
# Renames Program.cs to ProgramvX.X.cs before each build

# Get the current version from Program.cs
VERSION=$(grep -o 'APP_VERSION = "v[0-9.]*"' Program.cs | grep -o 'v[0-9.]*' | tr -d '"')

# If no version found, use timestamp
if [ -z "$VERSION" ]; then
    VERSION="v$(date +%Y%m%d_%H%M%S)"
fi

# Create archive directory if it doesn't exist
mkdir -p archive

# Copy current Program.cs to archive with version name
if [ -f "Program.cs" ]; then
    ARCHIVE_NAME="archive/Program_${VERSION}_$(date +%Y%m%d_%H%M%S).cs"
    cp Program.cs "$ARCHIVE_NAME"
    echo "✅ Archived: $ARCHIVE_NAME"
else
    echo "❌ Error: Program.cs not found"
    exit 1
fi
