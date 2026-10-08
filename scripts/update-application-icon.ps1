param([Parameter(Mandatory=$true)][string]$SourcePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) 'src\ZemaxMCP.Launcher\Assets'
$target=Join-Path $assetDirectory 'ZemaxMCP.ico'
$source=[Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourcePath).Path)
$sizes=@(16,20,24,32,40,48,64,96,128,256)
$frames=[Collections.Generic.List[byte[]]]::new()
try {
 foreach($size in $sizes){
  $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  $png=[IO.MemoryStream]::new()
  try{
   $graphics.Clear([Drawing.Color]::Transparent)
   $graphics.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
   $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
   $scale=[Math]::Min($size/$source.Width,$size/$source.Height)
   $width=$source.Width*$scale; $height=$source.Height*$scale
   $graphics.DrawImage($source,[Drawing.RectangleF]::new(($size-$width)/2,($size-$height)/2,$width,$height))
   $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
   $frames.Add($png.ToArray())
   if($size -eq 256){$bitmap.Save((Join-Path $assetDirectory 'IconPreview.png'),[Drawing.Imaging.ImageFormat]::Png)}
  } finally {$png.Dispose();$graphics.Dispose();$bitmap.Dispose()}
 }
 $stream=[IO.File]::Create($target);$writer=[IO.BinaryWriter]::new($stream)
 try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i -lt $sizes.Count;$i++){
   $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
   $writer.Write([byte]$dimension);$writer.Write([byte]$dimension)
   $writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32)
   $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
   $offset+=$frames[$i].Length
  }
  foreach($frame in $frames){$writer.Write([byte[]]$frame)}
 } finally {$writer.Dispose();$stream.Dispose()}
 for($i=0;$i -lt $sizes.Count;$i++){
  $pngStream=[IO.MemoryStream]::new($frames[$i]);$frameImage=[Drawing.Image]::FromStream($pngStream)
  try{
   if($frameImage.Width -ne $sizes[$i] -or $frameImage.Height -ne $sizes[$i]){throw 'Invalid PNG icon frame'}
   if(([Drawing.Bitmap]$frameImage).GetPixel(0,0).A -ne 0){throw 'Icon corner must remain transparent'}
  }finally{$frameImage.Dispose();$pngStream.Dispose()}
 }
 # GDI+ does not consistently select 256px PNG frames; Explorer uses them directly.
 foreach($size in @(16,24,32,48)){$icon=[Drawing.Icon]::new($target,$size,$size);try{if($icon.Width -ne $size){throw "Icon size mismatch: $size"}}finally{$icon.Dispose()}}
 Write-Output "Generated validated 32-bit application icon: $($sizes -join ', ') px. Original aspect ratio preserved."
} finally {$source.Dispose()}
