
# Set the path to your project folder
$sourceFolder = ".\"
$outputFile = ".\AllCodeCombined.txt"

# Get all source code files (e.g., .cs, .json)
$files = Get-ChildItem -Path $sourceFolder -Recurse -File | Where-Object {
    $_.Extension -in ".cs", ".json"
}

# Clear or create the output file
if (Test-Path $outputFile) {
    Clear-Content $outputFile
} else {
    New-Item -ItemType File -Path $outputFile -Force
}

# Append each file's content to the output file
foreach ($file in $files) {
    Add-Content -Path $outputFile -Value "`n`n# ===== File: $($file.FullName) =====`n"
    # Use -Raw to read the file content as a single string, which is more efficient
    Get-Content $file.FullName -Raw | Add-Content -Path $outputFile
}
