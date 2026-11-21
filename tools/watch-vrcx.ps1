# Logs when new VRChat_*.png files appear/change in a camera roll and when VRCX's iTXt 'Description' chunk shows up.
# Usage: powershell -File watch-vrcx.ps1 -Root <camera roll folder> -Minutes 15 -Log <log file>
param([Parameter(Mandatory)][string]$Root, [int]$Minutes = 15, [Parameter(Mandatory)][string]$Log)
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Text;
public static class PngProbe {
  // Returns "desc" if an iTXt/tEXt Description chunk exists, "nodesc" if a complete PNG without it, "partial" if truncated/unreadable.
  public static string Probe(string path){
    try {
      using(var fs=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
        var lat=Encoding.GetEncoding(28591); var hdr=new byte[8];
        if(fs.Read(hdr,0,8)<8) return "partial";
        while(true){
          var b=new byte[8]; if(fs.Read(b,0,8)<8) return "partial";
          long len=((long)b[0]<<24)|((long)b[1]<<16)|((long)b[2]<<8)|b[3];
          string type=Encoding.ASCII.GetString(b,4,4);
          if(type=="iTXt"||type=="tEXt"){
            var d=new byte[Math.Min(len,64)]; fs.Read(d,0,d.Length);
            int z=Array.IndexOf(d,(byte)0); string key=lat.GetString(d,0,z<0?d.Length:z);
            if(key=="Description") return "desc";
            fs.Position+= len-d.Length+4;
          } else if(type=="IEND") return "nodesc";
          else fs.Position+=len+4;
          if(fs.Position>fs.Length) return "partial";
        }
      }
    } catch { return "partial"; }
  }
}
'@
$start = Get-Date
$seen = @{}
function Out([string]$s){ $line = "{0:HH:mm:ss.fff}  {1}" -f (Get-Date), $s; Add-Content -Path $Log -Value $line }
Out "monitor started on $Root for $Minutes min"
while(((Get-Date) - $start).TotalMinutes -lt $Minutes){
  $dirs = @($Root) + @(Get-ChildItem $Root -Directory | Where-Object { $_.Name -match '^\d{4}-\d{2}$' } | Sort-Object Name -Descending | Select-Object -First 2 -ExpandProperty FullName)
  foreach($d in $dirs){
    foreach($f in [IO.Directory]::EnumerateFiles($d, 'VRChat_*.png')){
      $fi = [IO.FileInfo]::new($f)
      if($fi.CreationTime -lt $start -and -not $seen.ContainsKey($f)){ continue }
      $state = "{0}|{1:o}|{2}" -f $fi.Length, $fi.LastWriteTime, [PngProbe]::Probe($f)
      if(-not $seen.ContainsKey($f)){
        $seen[$f] = $state
        Out ("NEW     {0}  created={1:HH:mm:ss.fff} (+{2:N0} ms ago)  size={3}  {4}" -f $fi.Name, $fi.CreationTime, ((Get-Date)-$fi.CreationTime).TotalMilliseconds, $fi.Length, $state.Split('|')[2])
      } elseif($seen[$f] -ne $state){
        $seen[$f] = $state
        Out ("CHANGE  {0}  +{1:N0} ms after create  size={2}  mtime={3:HH:mm:ss.fff}  {4}" -f $fi.Name, ((Get-Date)-$fi.CreationTime).TotalMilliseconds, $fi.Length, $fi.LastWriteTime, $state.Split('|')[2])
      }
    }
  }
  Start-Sleep -Milliseconds 100
}
Out "monitor finished"
