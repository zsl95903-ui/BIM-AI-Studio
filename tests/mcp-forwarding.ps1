[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$dll=Join-Path $repo 'src/RevitAi.McpServer/bin/Release/net10.0/RevitAi.McpServer.dll'
function Assert($Condition,$Message){if(-not $Condition){throw $Message}}
function Test-Forward($Name,$Arguments,$ExpectedTool,$ExpectedArguments){
 $pipeName='bim-test-'+[Guid]::NewGuid().ToString('N')
 $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
 $start.ArgumentList.Add($dll)
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $start.Environment['REVIT_AI_BRIDGE_PIPE']=$pipeName
 $process=[Diagnostics.Process]::Start($start)
 try{
  $process.StandardInput.WriteLine((@{jsonrpc='2.0';id=1;method='tools/call';params=@{name=$Name;arguments=$Arguments}}|ConvertTo-Json -Depth 30 -Compress))
  $requests=@()
  for($i=0;$i -lt 2;$i++){
   $pipe=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,8,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
   try{
    $connection=$pipe.WaitForConnectionAsync();Assert ($connection.Wait(10000)) 'MCP connection timed out'
    $reader=[IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,1024,$true)
    $writer=[IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),1024,$true);$writer.AutoFlush=$true
    $request=$reader.ReadLine()|ConvertFrom-Json -AsHashtable;$requests+=,$request
    $payload=if($request.tool -eq 'get_document_snapshot'){@{documentGuid='test-document'}}else{@{ok=$true}}
    $writer.WriteLine((@{success=$true;requestId=$request.requestId;payload=$payload}|ConvertTo-Json -Depth 20 -Compress))
   }finally{$pipe.Dispose()}
  }
  $actual=$requests[-1]
  Assert ($actual.tool -eq $ExpectedTool) 'Tool name changed during forwarding'
  Assert (($actual.arguments|ConvertTo-Json -Depth 30 -Compress) -eq ($ExpectedArguments|ConvertTo-Json -Depth 30 -Compress)) 'Arguments were lost or wrapped'
  Assert ($actual.expectedDocumentGuid -eq 'test-document') 'Document identity was not pinned'
  Assert ($actual.protocolVersion -eq '3.0') 'Protocol version missing'
  $response=$process.StandardOutput.ReadLine()|ConvertFrom-Json
  Assert (-not $response.result.isError) 'MCP returned error'
 }finally{if(-not $process.HasExited){$process.Kill($true)};$process.Dispose()}
}
Test-Forward 'create_level' ([ordered]@{elevation=3;levelName='L2'}) 'create_level' ([ordered]@{elevation=3;levelName='L2'})
Test-Forward 'create_level' ([ordered]@{elevation=@(3,6);levelName=@('L2','L3')}) 'create_level' ([ordered]@{elevation=@(3,6);levelName=@('L2','L3')})
Test-Forward 'custom_tool' ([ordered]@{tool='must-not-reroute';arguments=@{nested=@(1,2)};flag=$false}) 'custom_tool' ([ordered]@{tool='must-not-reroute';arguments=@{nested=@(1,2)};flag=$false})
Test-Forward 'revit_invoke_tool' ([ordered]@{tool='create_level';arguments=[ordered]@{elevation=3;levelName='L2'}}) 'create_level' ([ordered]@{elevation=3;levelName='L2'})
Write-Output 'MCP: scalar, array, nested arguments, wrapper, document binding and protocol passed.'
function Test-Cancel {
 $pipeName='bim-cancel-'+[Guid]::NewGuid().ToString('N')
 $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.ArgumentList.Add($dll)
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $start.Environment['REVIT_AI_BRIDGE_PIPE']=$pipeName
 $main=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,8,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
 $cancelPipe=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,8,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
 $process=[Diagnostics.Process]::Start($start)
 try {
  $connected=$main.WaitForConnectionAsync()
  $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_document_snapshot","arguments":{}}}')
  Assert ($connected.Wait(10000)) 'No pending MCP call'
  $reader=[IO.StreamReader]::new($main,[Text.Encoding]::UTF8,$false,1024,$true)
  $initial=$reader.ReadLine()|ConvertFrom-Json
  $cancelling=$cancelPipe.WaitForConnectionAsync()
  $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}')
  Assert ($cancelling.Wait(10000)) 'Cancellation did not reach the bridge'
  $cancelReader=[IO.StreamReader]::new($cancelPipe,[Text.Encoding]::UTF8,$false,1024,$true)
  $cancelRequest=$cancelReader.ReadLine()|ConvertFrom-Json
  Assert ($cancelRequest.type -eq 'request.cancel') 'Wrong cancellation envelope'
  Assert ($cancelRequest.requestId -eq $initial.requestId) 'Wrong request cancelled'
  $writer=[IO.StreamWriter]::new($cancelPipe,[Text.UTF8Encoding]::new($false),1024,$true);$writer.AutoFlush=$true;$writer.WriteLine('{"success":true,"state":"cancelling"}')
  $response=$process.StandardOutput.ReadLine()|ConvertFrom-Json
  Assert $response.result.isError 'Ambiguous cancellation reported success'
  $outcome=$response.result.content[0].text|ConvertFrom-Json
  Assert ($outcome.errorCode -eq 'outcome_unknown') 'Lost unknown outcome state'
  Assert ($outcome.requestId -eq $initial.requestId) 'Lost recovery request id'
 } finally { $main.Dispose();$cancelPipe.Dispose();if(-not $process.HasExited){$process.Kill($true)};$process.Dispose() }
}
Test-Cancel
Write-Output 'MCP: concurrent cancellation and ambiguous outcome recovery passed.'
