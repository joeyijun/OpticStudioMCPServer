param([Parameter(Mandatory=$true)][string]$ZosApiBuildRoot,
    [string]$ReportDirectory='', [string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$ReportDirectory){$ReportDirectory=Join-Path $PSScriptRoot 'fixtures/localized-analysis-reports'}
[void][Reflection.Assembly]::LoadFrom((Join-Path $ZosApiBuildRoot 'ZOSAPI_Interfaces.dll'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root "src/ZemaxMCP.Server/bin/$Configuration/net48/ZemaxMCP.Worker.exe"))
$cases=@(
 @('FieldCurvature','FieldCurvatureDistortionTool','ParseTextFile'),
 @('FocalShiftDiagram','ChromaticFocalShiftTool','ParseChromaticFocalShiftTextFile'),
 @('LateralColor','LateralColorTool','ParseLateralColorTextFile'),
 @('RelativeIllumination','RelativeIlluminationTool','ParseRelativeIlluminationTextFile'),
 @('OpdFan','OpticalPathFanTool','ParseOpdFanTextFile'))
foreach($case in $cases){
 $file=Get-ChildItem -LiteralPath $ReportDirectory -Filter ($case[0]+'.txt')|Select-Object -First 1
 if(!$file){$file=Get-ChildItem -LiteralPath $ReportDirectory -Filter ('ZemaxMCP-parser-'+$case[0]+'-*.txt')|Select-Object -Last 1}
 if(!$file){throw ('Missing report '+$case[0])}
 $type=$assembly.GetType('ZemaxMCP.Server.Tools.Analysis.'+$case[1])
 $method=$type.GetMethod($case[2],[Reflection.BindingFlags]'NonPublic,Static')
 $result=$method.Invoke($null,@($file.FullName))
 if(!$result.Success -or ($result|ConvertTo-Json -Depth 40) -match '"(?:NaN|Infinity|-Infinity)"'){throw ('Invalid numeric result '+$case[0])}
 switch($case[0]){
  FieldCurvature {if($result.MaximumFieldDeg -ne 20 -or $result.ShiftUnits -ne 'Millimeters' -or
   [Math]::Abs($result.WavelengthData[0].TangentialShift[0]-0.19677233) -gt 1e-8){throw 'FieldCurvature metadata/numeric mismatch'}}
  FocalShiftDiagram {if($result.MaximumFocalShiftRange -ne 80.3079 -or $result.DataPoints -lt 2 -or
   $result.MaximumFocalShiftRangeUnits -ne 'μm' -or $result.ShiftUnits -ne 'Millimeters'){throw 'Focal shift value or distinct range/curve units mismatch'}}
  LateralColor {if($result.ShortWavelength -ne 0.48 -or $result.LongWavelength -ne 0.65){throw 'Duplicate localized wavelength labels not recovered'}}
  RelativeIllumination {if($result.Wavelength -ne 0.55 -or $result.RelativeIllumination[0] -ne 1){throw 'Relative illumination mismatch'}}
  OpdFan {if($result.Fields.Count -lt 1 -or $result.Units -ne 'Waves'){throw 'OPD fields/units mismatch'}}
 }
 Write-Output ('[PASS] Localized report numeric/metadata: '+$case[0])
}
