# Set the path to your project folder
$sourceFolder = ".\"
$outputFile = ".\AllCodeCombined.txt"

# --- FIX FOR LONG PATHS ---
$fullSourcePath = (Resolve-Path -LiteralPath $sourceFolder).ProviderPath
$longPathSource = "\\?\$fullSourcePath"

# --- DEFINITIVE FILE DISCOVERY LOGIC ---
# Get ALL files recursively first, then pipe them to a robust filter.
$files = Get-ChildItem -LiteralPath $longPathSource -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    # Filter 1: The full path must NOT contain '\bin\' or '\obj\'
    ($_.FullName -notmatch '\\bin\\|\\obj\\') -and
    # Filter 2: The file extension must be .cs or .json
    ($_.Extension -in ".cs", ".json")
}

# Clear or create the output file
if (Test-Path $outputFile) {
    Clear-Content $outputFile
} else {
    New-Item -ItemType File -Path $outputFile -Force
}

# Append each file's content to the output file
foreach ($file in $files) {
    Write-Host "Adding: $($file.FullName)" -ForegroundColor Green

    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($null -ne $content) {
        Add-Content -Path $outputFile -Value "`n`n# ===== File: $($file.FullName) =====`n"
        Add-Content -Path $outputFile -Value $content
    }
}

Write-Host "✅ Done! All code has been combined into $outputFile"
