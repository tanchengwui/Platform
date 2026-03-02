Write-Host "🚀 Starting Platform..."

# Kill old dotnet processes (optional but recommended)
Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force

# Start API
Start-Process powershell -ArgumentList "dotnet run --project .\src\Platform.Api --launch-profile http"

# Start Runtime
Start-Process powershell -ArgumentList "dotnet run --project .\src\Platform.Runtime --launch-profile http"

# Start ServiceCenter
Start-Process powershell -ArgumentList "dotnet run --project .\src\Platform.ServiceCenter --launch-profile http"

Write-Host "✅ All services starting..."
Write-Host "API: http://localhost:5002"
Write-Host "Runtime: http://localhost:5225"
Write-Host "ServiceCenter: http://localhost:5034"