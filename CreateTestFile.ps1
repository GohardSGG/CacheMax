# Create test file
if (!(Test-Path "A:\Test")) {
    New-Item -ItemType Directory -Path "A:\Test" -Force | Out-Null
}

$data = [byte[]]::new(1MB)
[System.Random]::new().NextBytes($data)

$stream = [System.IO.File]::OpenWrite("A:\Test\benchmark_test.dat")
try {
    for ($i = 0; $i -lt 32; $i++) {
        $stream.Write($data, 0, $data.Length)
    }
} finally {
    $stream.Close()
}

Write-Host "Created 32MB test file: A:\Test\benchmark_test.dat"