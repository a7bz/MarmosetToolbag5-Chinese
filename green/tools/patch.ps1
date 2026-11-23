# Marmoset Toolbag 5 汉化补丁 - 绿色版核心脚本
#
# 由 安装.bat / 卸载.bat 调用，不单独双击运行。
# 设计原则：
#   * 只做「备份 + 就地打补丁」两件事，不修改注册表，卸载只需还原备份。
#   * 每次操作前用 SHA256 校验目标文件，版本不符就停下，绝不写坏程序。
#   * 补丁数据来自 dist\patch.json（偏移 + 原字节 + 新字节 + 长度常量），
#     所以本补丁不包含游戏本体文件，体积很小。
#   * 备份放在补丁目录的 backup 子目录，便于整个文件夹拷走。

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('install', 'uninstall', 'status', 'lang-en', 'lang-zh')]
    [string]$Action
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 常量

$PatchDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir  = Split-Path -Parent $PatchDir
$DistDir  = Join-Path $RootDir 'dist'
$BackupDir = Join-Path $RootDir 'backup'
$InfoFile = Join-Path $BackupDir 'install-info.txt'
$PatchJson = Join-Path $DistDir 'patch.json'
$ZhTextFile = Join-Path $DistDir 'english.txt'

$ExeRel  = 'toolbag.exe'
$TextRel = 'data\text\english.txt'

# 由 patch.json 填充
$OrigExeSha  = $null
$OrigTextSha = $null
$ZhExeSha    = $null
$ZhTextSha   = $null
$PatchData   = $null

# ---------------------------------------------------------------- 输出

function Say([string]$msg, [ConsoleColor]$color = 'Gray') {
    Write-Host $msg -ForegroundColor $color
}

function Ok([string]$msg)   { Say "  [完成] $msg" 'Green' }
function Warn([string]$msg) { Say "  [注意] $msg" 'Yellow' }
function Bad([string]$msg)  { Say "  [错误] $msg" 'Red' }

function Fail([string]$msg) {
    Bad $msg
    Say ''
    Read-Host '按回车键退出'
    exit 1
}

# ---------------------------------------------------------------- 工具

function Get-Sha256([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Load-PatchData {
    if ($script:PatchData) { return $script:PatchData }

    if (-not (Test-Path -LiteralPath $PatchJson)) {
        Fail "补丁数据缺失：$PatchJson`n请确认补丁目录完整（dist 子目录）。"
    }
    if (-not (Test-Path -LiteralPath $ZhTextFile)) {
        Fail "汉化文本缺失：$ZhTextFile`n请确认补丁目录完整（dist 子目录）。"
    }

    $pd = Get-Content -LiteralPath $PatchJson -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $pd.patches) { Fail '补丁数据无效（patches 为空）。' }

    $script:PatchData   = $pd
    $script:OrigExeSha  = $pd.exe_sha256_orig.ToUpper()
    $script:OrigTextSha = $pd.text_sha256_orig.ToUpper()
    $script:ZhExeSha    = $pd.exe_sha256_new.ToUpper()
    $script:ZhTextSha   = $pd.text_sha256_new.ToUpper()
    return $pd
}

# 把 patch.json 里的补丁应用到 exe（就地修改），返回新的 SHA256
function Apply-ExePatch([string]$exePath, $pd) {
    $bytes = [System.IO.File]::ReadAllBytes($exePath)
    $n = 0
    foreach ($p in $pd.patches) {
        $off = [int]$p[0]
        $old = [Convert]::FromBase64String([string]$p[1])
        $new = [Convert]::FromBase64String([string]$p[2])

        # 原字节必须完全匹配，否则说明版本不对，立刻中止（此时文件还没被写过）
        if ($off + $old.Length -gt $bytes.Length) {
            throw ("补丁偏移越界：{0}" -f $off)
        }
        for ($i = 0; $i -lt $old.Length; $i++) {
            if ($bytes[$off + $i] -ne $old[$i]) {
                throw ("原版校验失败（偏移 {0}），补丁与当前程序版本不匹配。" -f $off)
            }
        }

        # 槽位 = 字符串本身 + 紧随其后的 NUL 填充
        $slotEnd = $off + $old.Length
        while ($slotEnd -lt $bytes.Length -and $bytes[$slotEnd] -eq 0) { $slotEnd++ }
        $slot = $slotEnd - $off
        if ($new.Length -gt $slot - 1) {
            throw ("译文超出可用空间（偏移 {0}，需要 {1}，可用 {2}）。" -f $off, $new.Length, ($slot - 1))
        }

        [Array]::Copy($new, 0, $bytes, $off, $new.Length)
        for ($i = $new.Length; $i -lt $slot; $i++) { $bytes[$off + $i] = 0 }
        $n++
    }

    # 变长译文必须同时改写调用点的「编译期长度立即数」，否则界面只显示前几个字
    foreach ($e in $pd.len_edits) {
        $imm = [int]$e[0]
        $ov  = [int]$e[1]
        $nv  = [int]$e[2]
        if ($imm + 4 -gt $bytes.Length) { throw ("长度常量越界：{0}" -f $imm) }
        $cur = [BitConverter]::ToUInt32($bytes, $imm)
        if ($cur -ne [uint32]$ov) {
            throw ("长度常量校验失败（偏移 {0}：{1} != {2}）。" -f $imm, $cur, $ov)
        }
        $nb = [BitConverter]::GetBytes([uint32]$nv)
        [Array]::Copy($nb, 0, $bytes, $imm, 4)
    }

    # 先写临时文件再替换，避免中断留下半个文件。
    # 目标文件可能被杀毒软件或残留进程短暂占用，因此带重试。
    $tmp = "$exePath.zhnew"
    [System.IO.File]::WriteAllBytes($tmp, $bytes)

    $lastErr = $null
    for ($try = 1; $try -le 10; $try++) {
        try {
            [System.IO.File]::Copy($tmp, $exePath, $true)
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            $lastErr = $null
            break
        }
        catch {
            $lastErr = $_
            Start-Sleep -Milliseconds 500
        }
    }
    if ($lastErr) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "无法写入 $exePath（文件被占用）。请关闭 Toolbag、杀毒软件后重试。"
    }

    Say ("  已就地应用 {0} 处文本补丁、{1} 处长度常量。" -f $n, $pd.len_edits.Count)
    return (Get-Sha256 $exePath)
}

function Find-InstallDir {
    # 1) 上次安装记录
    if (Test-Path -LiteralPath $InfoFile) {
        foreach ($line in Get-Content -LiteralPath $InfoFile -Encoding UTF8) {
            if ($line -match '^dir=(.+)$') {
                $d = $Matches[1].Trim()
                if (Test-Path -LiteralPath (Join-Path $d $ExeRel)) { return $d }
            }
        }
    }

    # 2) 卸载注册表项（Toolbag 官方安装器写入）
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSET_Toolbag5',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MSET_Toolbag5',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MSET_Toolbag5'
    )
    foreach ($k in $keys) {
        if (-not (Test-Path $k)) { continue }
        $props = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue
        foreach ($cand in @($props.UninstallString, $props.InstallLocation)) {
            if ([string]::IsNullOrWhiteSpace($cand)) { continue }
            $cand = $cand.Trim().Trim('"')
            if (Test-Path -LiteralPath $cand -PathType Leaf) {
                $cand = Split-Path -Parent $cand
            }
            if (Test-Path -LiteralPath (Join-Path $cand $ExeRel)) { return $cand }
        }
    }

    # 3) 常见安装位置
    foreach ($drive in @('C:', 'D:', 'E:', 'F:', 'G:')) {
        $guess = Join-Path $drive 'Program Files\Marmoset\Toolbag 5'
        if (Test-Path -LiteralPath (Join-Path $guess $ExeRel)) { return $guess }
    }
    return $null
}

function Stop-Toolbag {
    $procs = Get-Process -Name 'toolbag' -ErrorAction SilentlyContinue
    if (-not $procs) { return $false }
    Say '  Toolbag 正在运行，正在关闭…' 'Yellow'
    foreach ($p in $procs) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    Start-Sleep -Seconds 2

    # 最多等 15 秒，确认进程真的退出了，否则替换 exe 会失败
    for ($i = 0; $i -lt 30; $i++) {
        $procs = Get-Process -Name 'toolbag' -ErrorAction SilentlyContinue
        if (-not $procs) { return $true }
        foreach ($p in $procs) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch { }
        }
        Start-Sleep -Milliseconds 500
    }

    if (Get-Process -Name 'toolbag' -ErrorAction SilentlyContinue) {
        Fail 'Toolbag 无法关闭，请手动退出后重试。'
    }
    return $true
}

# ---------------------------------------------------------------- 工作区标签

# 工作区标签就是 workspaces 目录下的 json 文件名，只能改文件名本身。
# 改名前必须同步 prefs.txt 里的 workspaceOrder / activeWorkspace，否则程序可能无法启动。
$WsMap = @(
    @('Animate', '动画'),
    @('Render', '渲染'),
    @('Setup', '设置'),
    @('Texture', '贴图'),
    @('Classic', '经典'),
    @('Workspace 1', '工作区 1')
)

function Get-WsBackupDir { return (Join-Path $BackupDir 'workspaces') }
function Get-WsManifest   { return (Join-Path (Get-WsBackupDir) 'renames.txt') }

function Find-ProfileDir {
    $cands = @()
    $cands += (Join-Path $env:LOCALAPPDATA 'Marmoset Toolbag 5')
    # 提权运行时可能切换了用户配置，退回到扫描其他用户目录
    $users = Split-Path -Parent $env:USERPROFILE
    if ($users -and (Test-Path -LiteralPath $users)) {
        Get-ChildItem -LiteralPath $users -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $cands += (Join-Path $_.FullName 'AppData\Local\Marmoset Toolbag 5')
        }
    }

    # 优先选择确实含有已知工作区的配置目录
    foreach ($c in $cands) {
        $ws = Join-Path $c 'workspaces'
        if (-not (Test-Path -LiteralPath $ws)) { continue }
        foreach ($pair in $WsMap) {
            if ((Test-Path -LiteralPath (Join-Path $ws ($pair[0] + '.json'))) -or
                (Test-Path -LiteralPath (Join-Path $ws ($pair[1] + '.json')))) { return $c }
        }
    }
    foreach ($c in $cands) {
        if (Test-Path -LiteralPath (Join-Path $c 'workspaces')) { return $c }
    }
    return $null
}

function Update-Prefs([string]$prefs, [hashtable]$map) {
    if (-not (Test-Path -LiteralPath $prefs) -or $map.Count -eq 0) { return }
    $raw = [System.IO.File]::ReadAllText($prefs, [System.Text.Encoding]::UTF8)
    $lines = $raw -split "`r`n"
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $ln = $lines[$i]
        if ($ln.StartsWith('workspaceOrder ')) {
            $val = $ln.Substring('workspaceOrder '.Length)
            $parts = @()
            foreach ($p in ($val -split ';')) {
                if ($p.Length -eq 0) { continue }
                $parts += $(if ($map.ContainsKey($p)) { $map[$p] } else { $p })
            }
            $lines[$i] = 'workspaceOrder ' + ($parts -join ';') + ';'
        }
        elseif ($ln.StartsWith('activeWorkspace ')) {
            $val = $ln.Substring('activeWorkspace '.Length).Trim()
            if ($map.ContainsKey($val)) { $lines[$i] = 'activeWorkspace ' + $map[$val] }
        }
    }
    [System.IO.File]::WriteAllText($prefs, ($lines -join "`r`n"),
        (New-Object System.Text.UTF8Encoding($false)))
}

function Set-WorkspaceLabels {
    $profile = Find-ProfileDir
    if (-not $profile) { Say '  未找到工作区配置，跳过改名。'; return 0 }
    $wsDir = Join-Path $profile 'workspaces'
    $prefs = Join-Path $profile 'prefs.txt'

    $bak = Get-WsBackupDir
    New-Item -ItemType Directory -Path $bak -Force | Out-Null
    $prefsBak = Join-Path $bak 'prefs.txt.orig'
    if ((Test-Path -LiteralPath $prefs) -and -not (Test-Path -LiteralPath $prefsBak)) {
        Copy-Item -LiteralPath $prefs -Destination $prefsBak -Force
    }

    $done = @()
    foreach ($pair in $WsMap) {
        $src = Join-Path $wsDir ($pair[0] + '.json')
        $dst = Join-Path $wsDir ($pair[1] + '.json')
        if (-not (Test-Path -LiteralPath $src)) { continue }
        if (Test-Path -LiteralPath $dst) { continue }
        try {
            Move-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop
            $done += ($pair[0] + '|' + $pair[1])
        }
        catch {
            Warn ("工作区改名失败：{0}" -f $pair[0])
        }
    }

    if ($done.Count -eq 0) { Say '  工作区标签已是中文，跳过。'; return 0 }

    # 先写清单再改 prefs，中途中断也能还原
    $done | Set-Content -LiteralPath (Get-WsManifest) -Encoding UTF8

    $map = @{}
    foreach ($line in $done) {
        $bar = $line.IndexOf('|')
        if ($bar -gt 0) { $map[$line.Substring(0, $bar)] = $line.Substring($bar + 1) }
    }
    Update-Prefs $prefs $map
    Ok ("已汉化 {0} 个工作区标签" -f $done.Count)
    return $done.Count
}

function Restore-WorkspaceLabels {
    $profile = Find-ProfileDir
    if (-not $profile) { return 0 }
    $wsDir = Join-Path $profile 'workspaces'
    $manifest = Get-WsManifest

    $back = @{}
    if (Test-Path -LiteralPath $manifest) {
        foreach ($line in Get-Content -LiteralPath $manifest -Encoding UTF8) {
            $bar = $line.IndexOf('|')
            if ($bar -gt 0) { $back[$line.Substring($bar + 1)] = $line.Substring(0, $bar) }
        }
    }

    $n = 0
    foreach ($k in @($back.Keys)) {
        $src = Join-Path $wsDir ($k + '.json')
        $dst = Join-Path $wsDir ($back[$k] + '.json')
        if (-not (Test-Path -LiteralPath $src)) { continue }
        if (Test-Path -LiteralPath $dst) { continue }
        try { Move-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop; $n++ } catch { }
    }

    Update-Prefs (Join-Path $profile 'prefs.txt') $back
    return $n
}

function Do-Install([string]$dir) {
    $pd = Load-PatchData

    $exePath  = Join-Path $dir $ExeRel
    $textPath = Join-Path $dir $TextRel

    if (-not (Test-Path -LiteralPath $exePath)) {
        Fail "目录中没有找到 $ExeRel：$dir"
    }
    if (-not (Test-Path -LiteralPath $textPath)) {
        Fail "目录中没有找到 $TextRel，安装可能不完整：$dir"
    }

    $curExeSha  = Get-Sha256 $exePath
    $curTextSha = Get-Sha256 $textPath

    # 是否已装过汉化
    $alreadyPatched = ($curExeSha -eq $ZhExeSha)

    if ($alreadyPatched) {
        Say '  当前已经是中文版，将重新覆盖安装。' 'Yellow'
    }
    elseif ($curExeSha -ne $OrigExeSha -or $curTextSha -ne $OrigTextSha) {
        Warn '当前文件与补丁支持的原版不一致：'
        Say  "    toolbag.exe  实际 $curExeSha"
        Say  "                 期望 $OrigExeSha"
        Say  "    english.txt  实际 $curTextSha"
        Say  "                 期望 $OrigTextSha"
        Say ''
        Say  "  本补丁针对 Toolbag $($pd.app_version)（build 5032）制作。" 'Yellow'
        Say  '  继续安装可能导致界面文字错位或程序异常。' 'Yellow'
        $ans = Read-Host '  仍要继续？输入 y 继续，其他键取消'
        if ($ans -notmatch '^(y|Y)') { Fail '已取消。' }
    }

    # ---- 备份（只备份真正的原版，绝不覆盖已有备份）
    if (-not (Test-Path -LiteralPath $BackupDir)) {
        New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    }
    $bakExe  = Join-Path $BackupDir 'toolbag.exe.orig'
    $bakText = Join-Path $BackupDir 'english.txt.orig'

    if (-not (Test-Path -LiteralPath $bakExe)) {
        if ($curExeSha -ne $OrigExeSha) {
            Fail "无法备份：当前 toolbag.exe 不是原版，且没有历史备份。`n请先还原原版或重新安装 Toolbag。"
        }
        Copy-Item -LiteralPath $exePath -Destination $bakExe -Force
        Ok '已备份 toolbag.exe'
    }
    else {
        Say '  已存在 toolbag.exe 备份，跳过。'
    }

    if (-not (Test-Path -LiteralPath $bakText)) {
        Copy-Item -LiteralPath $textPath -Destination $bakText -Force
        Ok '已备份 english.txt'
    }
    else {
        Say '  已存在 english.txt 备份，跳过。'
    }

    # ---- 写入汉化文件
    Say '  正在写入汉化文件…'
    if (-not $alreadyPatched) {
        try {
            Apply-ExePatch $exePath $pd | Out-Null
        }
        catch {
            # 打补丁失败时用备份还原，保证程序仍可用
            if (Test-Path -LiteralPath $bakExe) {
                Copy-Item -LiteralPath $bakExe -Destination $exePath -Force
                Warn '打补丁失败，已自动还原原版 toolbag.exe。'
            }
            Fail $_.Exception.Message
        }
    }
    Copy-Item -LiteralPath $ZhTextFile -Destination $textPath -Force

    # ---- 校验写入结果
    if ((Get-Sha256 $exePath) -ne $ZhExeSha) {
        Fail 'toolbag.exe 写入校验失败。请从 backup 目录手动还原。'
    }
    if ((Get-Sha256 $textPath) -ne $ZhTextSha) {
        Fail 'english.txt 写入校验失败。请从 backup 目录手动还原。'
    }

    # ---- 记录安装信息
    @(
        "dir=$dir"
        "installed=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        "version=1.0.0"
    ) | Set-Content -LiteralPath $InfoFile -Encoding UTF8

    # ---- 工作区标签（文件名即标签，需同步 prefs.txt）
    Set-WorkspaceLabels | Out-Null

    Ok '汉化安装完成'
}

# ---------------------------------------------------------------- 卸载

function Do-Uninstall {
    $bakExe  = Join-Path $BackupDir 'toolbag.exe.orig'
    $bakText = Join-Path $BackupDir 'english.txt.orig'

    if (-not (Test-Path -LiteralPath $bakExe) -or -not (Test-Path -LiteralPath $bakText)) {
        Fail "没有找到备份文件，无法卸载。`n备份目录：$BackupDir"
    }

    $dir = Find-InstallDir
    if (-not $dir) {
        Fail "没有找到 Toolbag 安装目录。`n请手动把以下文件复制回安装目录：`n  $bakExe  ->  toolbag.exe`n  $bakText ->  data\text\english.txt"
    }

    Stop-Toolbag | Out-Null

    $exePath  = Join-Path $dir $ExeRel
    $textPath = Join-Path $dir $TextRel

    Say '  正在还原英文版文件…'
    Copy-Item -LiteralPath $bakExe  -Destination $exePath  -Force
    Copy-Item -LiteralPath $bakText -Destination $textPath -Force

    # 备份是原版，还原后必然等于原版哈希；此处只做兜底提示
    $pd = $null
    try { $pd = Load-PatchData } catch { }
    if ($pd) {
        if ((Get-Sha256 $exePath) -ne $OrigExeSha) {
            Warn 'toolbag.exe 还原后校验不一致，请检查是否被其他程序占用。'
        }
        if ((Get-Sha256 $textPath) -ne $OrigTextSha) {
            Warn 'english.txt 还原后校验不一致。'
        }
    }

    if (Test-Path -LiteralPath $InfoFile) {
        Remove-Item -LiteralPath $InfoFile -Force -ErrorAction SilentlyContinue
    }

    # 工作区标签还原（文件名改回英文，同步 prefs.txt）
    $restored = Restore-WorkspaceLabels
    if ($restored -gt 0) { Ok ("已还原 {0} 个工作区标签" -f $restored) }

    Ok '已还原为英文版'
}

# ---------------------------------------------------------------- 状态

function Show-Status {
    $dir = Find-InstallDir
    if (-not $dir) {
        Warn '未检测到 Toolbag 5 安装目录。'
        return
    }

    $pd = $null
    try { $pd = Load-PatchData } catch { }

    $exePath  = Join-Path $dir $ExeRel

    $curExeSha = Get-Sha256 $exePath
    $isZh = $pd -and ($curExeSha -eq $ZhExeSha)
    $isEn = $pd -and ($curExeSha -eq $OrigExeSha)

    Say "  安装目录：$dir"
    if ($isZh)      { Say '  当前语言：中文（已汉化）' 'Green' }
    elseif ($isEn)  { Say '  当前语言：英文（原版）' 'Gray' }
    else            { Say '  当前语言：未知（文件已被修改）' 'Yellow' }

    $bakExe = Join-Path $BackupDir 'toolbag.exe.orig'
    if (Test-Path -LiteralPath $bakExe) { Say '  英文备份：有' }
    else                                { Say '  英文备份：无（尚未安装过）' }
}

# ---------------------------------------------------------------- 语言切换

function Switch-Language([string]$want) {
    $dir = Find-InstallDir
    if (-not $dir) { Fail '未检测到 Toolbag 5 安装目录。' }

    $pd = Load-PatchData

    $exePath  = Join-Path $dir $ExeRel
    $textPath = Join-Path $dir $TextRel
    $bakExe   = Join-Path $BackupDir 'toolbag.exe.orig'
    $bakText  = Join-Path $BackupDir 'english.txt.orig'

    $cur = Get-Sha256 $exePath
    $isZh = ($cur -eq $ZhExeSha)
    $isEn = ($cur -eq $OrigExeSha)

    if ($want -eq 'zh') {
        if ($isZh) { Say '  当前已经是中文版。' 'Green'; return }
        if (-not $isEn) {
            Fail "当前 toolbag.exe 既不是原版也不是中文版，无法安全切换。`n请先运行「卸载.bat」还原英文。"
        }
        Stop-Toolbag | Out-Null
        try {
            Apply-ExePatch $exePath $pd | Out-Null
        }
        catch {
            if (Test-Path -LiteralPath $bakExe) {
                Copy-Item -LiteralPath $bakExe -Destination $exePath -Force
            }
            Fail $_.Exception.Message
        }
        Copy-Item -LiteralPath $ZhTextFile -Destination $textPath -Force
        if ((Get-Sha256 $exePath) -ne $ZhExeSha) { Fail 'toolbag.exe 写入校验失败。' }
        Set-WorkspaceLabels | Out-Null
        Ok '已切换到中文'
    }
    else {
        if ($isEn) { Say '  当前已经是英文版。' 'Green'; return }
        if (-not (Test-Path -LiteralPath $bakExe)) {
            Fail '没有英文备份，无法切换。'
        }
        Stop-Toolbag | Out-Null
        Copy-Item -LiteralPath $bakExe  -Destination $exePath  -Force
        Copy-Item -LiteralPath $bakText -Destination $textPath -Force
        Restore-WorkspaceLabels | Out-Null
        Ok '已切换到英文'
    }
}

# ---------------------------------------------------------------- 主流程

Say ''
Say '==============================================' 'Cyan'
Say '  Marmoset Toolbag 5 简体中文补丁（绿色版）' 'Cyan'
Say '==============================================' 'Cyan'
Say ''

$dir = Find-InstallDir
if ($Action -in @('install', 'uninstall', 'lang-en', 'lang-zh') -and $dir) {
    Say "  检测到安装目录：$dir"
    Say ''
}

switch ($Action) {
    'install' {
        if (-not $dir) {
            $dir = Read-Host '  未自动检测到安装目录，请手动输入 Toolbag 5 路径'
            $dir = $dir.Trim().Trim('"')
        }
        Stop-Toolbag | Out-Null
        Do-Install $dir
        Say ''
        Say '  现在可以启动 Toolbag 查看中文界面。' 'Green'
        Say "  如需还原英文，运行「卸载.bat」。" 'Gray'
    }
    'uninstall' {
        Do-Uninstall
        Say ''
        Say '  已恢复英文界面。' 'Green'
    }
    'status' { Show-Status }
    'lang-en' { Switch-Language 'en' }
    'lang-zh' { Switch-Language 'zh' }
}

Say ''
Read-Host '按回车键退出'
