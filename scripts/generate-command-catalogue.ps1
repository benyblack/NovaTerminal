#Requires -Version 5.1
<#
.SYNOPSIS
    Generates the bundled offline command-knowledge catalogue from a tldr-pages checkout.

.DESCRIPTION
    Command Assist V2 Phase 4b (docs/plans/2026-08-01-command-assist-v2-plan.md, Phase 4 task 3).
    Reads the markdown pages of a tldr-pages checkout and emits
    assets/command-knowledge/command-catalogue.json, which NovaTerminal.CommandAssist embeds and
    CommandKnowledgeService serves as Help docs and Recipe rows.

    SELECTION POLICY (deterministic, reviewable, and the reason this is a curated list rather than
    "everything tldr has"):
      1. $CoreCommands below - a hand-picked list of command tokens a terminal user actually reaches
         for. tldr ships ~4,600 common pages; most of them are tools nobody in this app's audience
         has installed, and a catalogue that answers "help for gitlab-ci-local" while doubling the
         asset is a worse catalogue.
      2. Every `git-*` page, expanded to a two-token entry (`git-rebase.md` -> `git rebase`). The
         subcommand is where git's real surface lives; `git` alone is close to useless as a help
         target.
      3. $SupplementPath - a small hand-authored file for commands tldr does not cover at all
         (today: Get-Process, Get-Service). Those entries are marked `"o": "nova"` in the asset so
         the CC-BY-SA attribution stays honest about what it does and does not cover.

    PAGE PRIORITY: common, linux, windows, osx. "Common pages first" is the plan's wording and it is
    also the right default - a page under pages/common describes the portable tool, and the
    platform directories mostly hold either platform-only tools (Get-ChildItem, systemctl) or
    same-name-different-tool collisions (pages/windows/find.md is FIND.EXE, not GNU find). Highest
    priority wins; a token present in several directories is emitted once.

    The source directory a page came from becomes the entry's shell hint: windows -> pwsh,
    linux/osx -> bash, common -> none. CommandKnowledgeService orders shell-matching rows first,
    which is the behavior SeedRecipeProviderTests used to pin.

.PARAMETER TldrPath
    Path to a tldr-pages checkout (git clone --depth 1 https://github.com/tldr-pages/tldr).

.PARAMETER OutputPath
    Where to write the catalogue. Defaults to assets/command-knowledge/command-catalogue.json
    relative to the repository root.

.PARAMETER SupplementPath
    Hand-authored entries merged into the output. Defaults to
    assets/command-knowledge/command-catalogue-supplement.json.

.EXAMPLE
    # Regenerate the committed asset (run from anywhere; paths are resolved off this script):
    git clone --depth 1 https://github.com/tldr-pages/tldr "$env:TEMP/tldr"
    pwsh -File scripts/generate-command-catalogue.ps1 -TldrPath "$env:TEMP/tldr"

    The generator is deterministic apart from the `generatedFrom` header field, which records the
    source checkout's commit. Re-run it when tldr-pages has moved on or when $CoreCommands changes,
    review the diff, and commit the asset together with whatever prompted the regeneration.

.NOTES
    LICENSING. tldr-pages content is CC-BY-SA 4.0
    (https://github.com/tldr-pages/tldr/blob/main/LICENSE.md). The attribution and licence URL are
    written into the asset header by this script, embedded into NovaTerminal.CommandAssist, and
    surfaced to the user in the Command Assist Help popup footer. Do not strip the header.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TldrPath,

    [string] $OutputPath,

    [string] $SupplementPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'assets\command-knowledge\command-catalogue.json'
}
if (-not $SupplementPath) {
    $SupplementPath = Join-Path $repoRoot 'assets\command-knowledge\command-catalogue-supplement.json'
}

$pagesRoot = Join-Path $TldrPath 'pages'
if (-not (Test-Path $pagesRoot)) {
    throw "No 'pages' directory under '$TldrPath'. Point -TldrPath at a tldr-pages checkout."
}

# Page directories in priority order. See the selection policy in the header comment.
$PageDirectories = @(
    @{ Name = 'common';  Shell = $null   },
    @{ Name = 'linux';   Shell = 'bash'  },
    @{ Name = 'windows'; Shell = 'pwsh'  },
    @{ Name = 'osx';     Shell = 'bash'  }
)

# Tokens that jump pages/windows to the front of that order.
#
# "Common first" is right for portable tools and wrong for the handful of names cmd.exe and a POSIX
# system both use for entirely different programs. `dir` under pages/linux is an alias stub for
# `ls -C --escape`; `where` under pages/common is the one-example plan9 utility. On a terminal whose
# first-run shell is PowerShell, answering "help for dir" with GNU ls flags is not a near miss, it
# is the wrong tool. Only names where the collision is real are listed - `find` and `sort` stay
# common-first on purpose, because GNU find and GNU sort are what a user asking about them means far
# more often than FIND.EXE and SORT.EXE are.
$WindowsFirstCommands = @(
    'dir', 'copy', 'move', 'del', 'ren', 'type', 'cls', 'where', 'fc', 'sc', 'attrib', 'clip',
    'assoc', 'setx', 'findstr', 'robocopy', 'xcopy', 'tasklist', 'taskkill', 'netsh', 'reg',
    'wmic', 'chkdsk', 'sfc', 'diskpart', 'systeminfo', 'wsl', 'ipconfig', 'tracert'
)

# At most this many examples per entry. tldr pages run to eight or nine; six is the plan's cap and
# is already more rows than the Help popup shows without scrolling.
$MaxExamples = 6

# Entries with no usable example at all are dropped: a catalogue row whose only content is a
# one-line summary is not what Help promises, and the empty state is a more honest answer. One is
# the floor rather than the plan's "3-6" because a handful of genuinely simple tools (`head`,
# `pwd`) have a single tldr example and dropping them would be a strictly worse catalogue; the cap
# above is what the "3-6" range is really about.
$MinExamples = 1

# ---------------------------------------------------------------------------------------------
# The curated core. Grouped for review, deduplicated below. Every name here is looked up as
# "<name>.md" (lowercased, spaces -> hyphens) in the page directories in priority order; a name
# with no page anywhere is reported at the end of the run rather than failing the generation, so
# this list can name a command tldr has not documented yet without breaking the build.
# ---------------------------------------------------------------------------------------------
$CoreCommands = @(
    # Files and directories
    'ls', 'cd', 'pwd', 'cp', 'mv', 'rm', 'mkdir', 'rmdir', 'touch', 'ln', 'stat', 'file', 'tree',
    'du', 'df', 'basename', 'dirname', 'readlink', 'realpath', 'shred', 'truncate', 'mktemp',

    # Text
    'cat', 'less', 'more', 'head', 'tail', 'grep', 'sed', 'awk', 'cut', 'sort', 'uniq', 'wc', 'tr',
    'paste', 'join', 'comm', 'diff', 'patch', 'tee', 'xargs', 'jq', 'yq', 'column', 'fmt', 'nl',
    'split', 'rev', 'tac', 'strings', 'iconv', 'base64', 'shuf', 'expand', 'unexpand',

    # Search
    'find', 'fd', 'rg', 'ag', 'ack', 'locate', 'which', 'whereis', 'whatis', 'fzf',

    # Archives and compression
    'tar', 'gzip', 'gunzip', 'zip', 'unzip', 'bzip2', 'xz', 'zstd', '7z', 'unrar', 'cpio',

    # Permissions, users, identity
    'chmod', 'chown', 'chgrp', 'umask', 'sudo', 'su', 'id', 'whoami', 'groups', 'passwd',
    'useradd', 'usermod', 'chsh', 'getent',

    # Processes and resources
    'ps', 'top', 'htop', 'btop', 'kill', 'pkill', 'pgrep', 'killall', 'jobs', 'nohup', 'nice',
    'renice', 'timeout', 'watch', 'time', 'strace', 'ltrace', 'lsof', 'uptime', 'free', 'vmstat',
    'iostat', 'pidof', 'fuser',

    # System
    'uname', 'hostname', 'date', 'cal', 'dmesg', 'lsblk', 'blkid', 'mount', 'umount', 'fdisk',
    'parted', 'systemctl', 'journalctl', 'service', 'crontab', 'at', 'shutdown', 'reboot',
    'sysctl', 'ulimit', 'lscpu', 'lspci', 'lsusb', 'modprobe', 'chroot', 'stty', 'env', 'export',

    # Network
    'ssh', 'scp', 'sftp', 'rsync', 'curl', 'wget', 'ping', 'traceroute', 'dig', 'nslookup', 'host',
    'netstat', 'ss', 'ip', 'ifconfig', 'route', 'arp', 'nc', 'nmap', 'telnet', 'ftp', 'iptables',
    'ufw', 'firewall-cmd', 'tcpdump', 'ssh-keygen', 'ssh-copy-id', 'ssh-agent', 'ssh-add',
    'openssl', 'mtr', 'ipcalc', 'whois',

    # Shell built-ins and everyday glue
    'echo', 'printf', 'alias', 'unalias', 'source', 'history', 'sleep', 'seq', 'yes', 'test',
    'expr', 'bc', 'clear', 'man', 'info', 'tldr', 'xdg-open', 'open', 'read', 'trap', 'exec',

    # Editors and multiplexers
    'vim', 'nvim', 'nano', 'emacs', 'tmux', 'screen', 'code', 'zellij',

    # Version control
    'git', 'gh', 'glab', 'svn', 'hg', 'diff3', 'delta',

    # Containers and orchestration
    'docker', 'docker-compose', 'podman', 'kubectl', 'helm', 'minikube', 'kind', 'k9s', 'kubectx',

    # Cloud and infrastructure
    'aws', 'az', 'gcloud', 'terraform', 'ansible', 'ansible-playbook', 'vagrant', 'packer',
    'pulumi', 'doctl', 'flyctl',

    # Package managers
    'apt', 'apt-get', 'dpkg', 'dnf', 'yum', 'rpm', 'pacman', 'zypper', 'apk', 'brew', 'snap',
    'flatpak', 'choco', 'scoop', 'winget', 'nix',

    # Languages, runtimes, build tools
    'python', 'python3', 'pip', 'pipx', 'poetry', 'uv', 'conda', 'node', 'npm', 'npx', 'pnpm',
    'yarn', 'bun', 'deno', 'tsc', 'go', 'cargo', 'rustc', 'rustup', 'dotnet', 'nuget', 'java',
    'javac', 'mvn', 'gradle', 'ruby', 'gem', 'bundle', 'rake', 'php', 'composer', 'perl', 'lua',
    'swift', 'make', 'cmake', 'ninja', 'gcc', 'clang', 'gdb', 'valgrind', 'ldd', 'objdump',

    # Data and databases
    'sqlite3', 'psql', 'mysql', 'mongosh', 'redis-cli', 'pg_dump', 'mysqldump',

    # Media, documents, crypto, misc
    'ffmpeg', 'magick', 'pandoc', 'gpg', 'md5sum', 'sha1sum', 'sha256sum', 'cksum', 'xxd',
    'hexdump', 'od', 'dd', 'cmp', 'pv', 'parallel', 'entr', 'direnv', 'asdf', 'bat', 'eza',
    'zoxide', 'ncdu', 'qrencode',

    # Windows console
    'cmd', 'powershell', 'pwsh', 'dir', 'copy', 'move', 'del', 'ren', 'type', 'cls', 'where',
    'findstr', 'tasklist', 'taskkill', 'ipconfig', 'tracert', 'netsh', 'reg', 'sc', 'wmic',
    'robocopy', 'xcopy', 'attrib', 'chkdsk', 'sfc', 'diskpart', 'schtasks', 'systeminfo', 'wsl',
    'clip', 'assoc', 'fc', 'setx',

    # PowerShell cmdlets
    'Get-ChildItem', 'Get-Content', 'Get-Command', 'Get-Help', 'Get-Location', 'Set-Location',
    'Get-Date', 'Get-History', 'Get-Alias', 'Set-Alias', 'Get-Clipboard', 'Set-Clipboard',
    'Get-FileHash', 'Get-Acl', 'Set-Acl', 'New-Item', 'Invoke-WebRequest', 'Invoke-Item',
    'Measure-Command', 'Measure-Object', 'Out-GridView', 'Out-String', 'Select-String',
    'Sort-Object', 'Where-Object', 'Start-Process', 'Start-Service', 'Stop-Service', 'Set-Service',
    'Test-Json', 'Test-NetConnection'
)

# ---------------------------------------------------------------------------------------------
# Parsing
# ---------------------------------------------------------------------------------------------

function ConvertTo-PageFileName {
    param([string] $Token)
    # tldr page files are lowercase and hyphen-separated: "Get-ChildItem" -> get-childitem.md,
    # "git rebase" -> git-rebase.md.
    return ($Token.ToLowerInvariant() -replace '\s+', '-') + '.md'
}

function Format-ExampleCommand {
    param([string] $Command)

    # tldr placeholder syntax, in the order the replacements have to happen.
    #
    #   {{[-i|--interactive]}}  an option with several spellings.
    #   {{ps|container ls}}     an alternation between whole words.
    #   {{value}}               an argument the user has to supply. Rendered <value> - the convention
    #                           the hand-written seed recipes used, and the one a user reading a
    #                           command line already knows means "replace me".
    #
    # The alternation rule is "the long option if there is one, otherwise the first alternative", and
    # both halves matter. Long-option-first turns {{[-i|--interactive]}} into `--interactive`, which
    # explains itself on the command line where `-i` does not. First-otherwise turns
    # {{[ps|container ls]}} into `docker ps` rather than `docker container ls`: tldr lists the
    # canonical spelling first, and taking the last alternative produced the form nobody types.
    $result = $Command
    $result = [regex]::Replace($result, '\{\{\[([^\]]*)\]\}\}', { param($m) Select-Alternative $m.Groups[1].Value })

    # One level of nesting is allowed inside the placeholder body, because tldr writes brace
    # expansions inside placeholders: `mkdir --parents {{path/to/{a,b}/{x,y,z}}}`. A plain
    # [^}]* stops at the first inner brace and leaves `{{` in the output - which then reaches the
    # user's command line verbatim. Pinned by CommandCatalogueAssetTests.
    $result = [regex]::Replace($result, '\{\{((?:[^{}]|\{[^{}]*\})*)\}\}', {
        param($m)
        $inner = $m.Groups[1].Value
        if ($inner -match '\|') { return Select-Alternative $inner }
        '<' + $inner + '>'
    })
    return $result.Trim()
}

function Select-Alternative {
    param([string] $Alternatives)

    $parts = $Alternatives -split '\|'
    foreach ($part in $parts) {
        if ($part.StartsWith('--')) { return $part }
    }
    return $parts[0]
}

function Format-Description {
    param([string] $Text)

    $result = $Text
    # tldr marks the letter an option is named after: "[i]dentity", "SSH [J]umping". Useful in a
    # rendered page, noise in a one-line row.
    $result = [regex]::Replace($result, '\[([A-Za-z0-9])\]', '$1')
    # Inline code spans: the row is already monospace-adjacent and the backticks read as literal.
    $result = $result -replace '`', ''
    # tldr example descriptions end with a colon that introduced the command block.
    $result = $result.TrimEnd()
    $result = $result -replace ':$', ''
    return $result.Trim()
}

function Read-TldrPage {
    param(
        [string] $Path,
        [string] $ShellKind
    )

    $lines = Get-Content -LiteralPath $Path -Encoding UTF8
    $token = $null
    $summary = $null
    $rawSummary = ''
    $examples = New-Object System.Collections.ArrayList
    $pendingDescription = $null

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0) { continue }

        if ($trimmed.StartsWith('# ')) {
            if (-not $token) { $token = $trimmed.Substring(2).Trim() }
            continue
        }

        if ($trimmed.StartsWith('> ')) {
            # The first > line is the page summary. The rest are "More information:", "Note:",
            # "See also:" and similar - useful on a rendered page, not in a one-line description.
            if (-not $summary) {
                $rawSummary = $trimmed.Substring(2)
                $summary = Format-Description $rawSummary
            }
            continue
        }

        if ($trimmed.StartsWith('- ')) {
            $pendingDescription = Format-Description $trimmed.Substring(2)
            continue
        }

        if ($trimmed.StartsWith('`') -and $trimmed.EndsWith('`') -and $trimmed.Length -gt 2) {
            if ($pendingDescription) {
                $command = Format-ExampleCommand $trimmed.Trim('`')
                # "View documentation for ..." rows are cross-references the tldr client renders as
                # a hint, not commands the user wants on their prompt: they are what an alias page
                # (`whoami`) and a disambiguation page (`snap`) contain instead of content. Matched
                # on the description rather than on the `tldr ` prefix, so the real tldr page keeps
                # its own examples.
                $isCrossReference = $pendingDescription -match '^View (the )?documentation (for|of)\b'
                if ($command.Length -gt 0 -and -not $isCrossReference) {
                    [void]$examples.Add([PSCustomObject]@{ c = $command; d = $pendingDescription })
                }
                $pendingDescription = $null
            }
            continue
        }
    }

    if (-not $token -or -not $summary) { return $null }

    # Alias pages ("This command is an alias of `id --user --name`") carry no examples of their own.
    # Reported rather than silently dropped so the caller can follow the alias.
    $aliasMatch = [regex]::Match($rawSummary, '^This command is an alias of `([^`]+)`')
    if ($aliasMatch.Success) {
        return [PSCustomObject]@{ IsAlias = $true; AliasTarget = $aliasMatch.Groups[1].Value; Token = $token }
    }

    if ($examples.Count -lt $MinExamples) { return $null }

    $selected = @($examples | Select-Object -First $MaxExamples)

    $entry = [ordered]@{ t = $token; d = $summary }
    if ($ShellKind) { $entry['s'] = $ShellKind }
    $entry['e'] = $selected
    return [PSCustomObject]$entry
}

function Find-TldrPages {
    param([string] $Token)

    # Every candidate, in priority order, not just the first. A page can exist and still be
    # unusable - pages/common/where.md is an alias stub while pages/windows/where.md is the real
    # thing - so "the highest-priority page" and "the highest-priority *usable* page" are different
    # questions and the second one is the one the catalogue wants.
    $fileName = ConvertTo-PageFileName $Token

    $order = $PageDirectories
    if ($WindowsFirstCommands -contains $Token) {
        $order = @($PageDirectories | Where-Object { $_.Name -eq 'windows' }) +
                 @($PageDirectories | Where-Object { $_.Name -ne 'windows' })
    }

    $results = @()
    foreach ($dir in $order) {
        $candidate = Join-Path (Join-Path $pagesRoot $dir.Name) $fileName
        if (Test-Path -LiteralPath $candidate) {
            $results += [PSCustomObject]@{ Path = $candidate; Shell = $dir.Shell; Directory = $dir.Name }
        }
    }
    return $results
}

function Resolve-TldrEntry {
    param(
        [string] $Token,
        [int] $Depth = 0
    )

    foreach ($page in (Find-TldrPages $Token)) {
        $parsed = Read-TldrPage -Path $page.Path -ShellKind $page.Shell
        if (-not $parsed) { continue }

        if ($parsed.PSObject.Properties['IsAlias']) {
            # Follow the alias to the page that actually has the examples, keeping the alias itself
            # as the entry token and saying so in the description. `whoami` genuinely is
            # `id --user --name`, and showing the target's examples under a description that names
            # the target is more useful than either dropping the entry or pretending the examples
            # spell the alias.
            if ($Depth -ge 2) { continue }
            $targetToken = $parsed.AliasTarget
            $targetCommand = ($targetToken -split '\s+')[0]
            foreach ($candidateToken in @($targetToken, $targetCommand)) {
                $resolved = Resolve-TldrEntry -Token $candidateToken -Depth ($Depth + 1)
                if ($resolved) {
                    $entry = [ordered]@{ t = $Token; d = ("Alias of {0}. {1}" -f $targetToken, $resolved.d) }
                    if ($page.Shell) { $entry['s'] = $page.Shell }
                    $entry['e'] = $resolved.e
                    return [PSCustomObject]$entry
                }
            }
            continue
        }

        return $parsed
    }

    return $null
}

# ---------------------------------------------------------------------------------------------
# Selection
# ---------------------------------------------------------------------------------------------

$tokens = New-Object System.Collections.Generic.List[string]
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($name in $CoreCommands) {
    if ($seen.Add($name)) { [void]$tokens.Add($name) }
}

# git subcommands: every git-*.md page in the priority directories, as a two-token entry. This is
# the one auto-expansion, and it is here because `git` alone answers almost nothing - the plan
# calls it out by name, and `git rebase` is the shape of the question a user actually has.
foreach ($dir in $PageDirectories) {
    $dirPath = Join-Path $pagesRoot $dir.Name
    if (-not (Test-Path $dirPath)) { continue }
    foreach ($file in (Get-ChildItem -LiteralPath $dirPath -Filter 'git-*.md' | Sort-Object Name)) {
        $sub = $file.BaseName.Substring(4)
        if ($sub.Length -eq 0) { continue }
        $token = 'git ' + $sub
        if ($seen.Add($token)) { [void]$tokens.Add($token) }
    }
}

$entries = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList
$thin = New-Object System.Collections.ArrayList

foreach ($token in $tokens) {
    if ((Find-TldrPages $token).Count -eq 0) { [void]$missing.Add($token); continue }

    $entry = Resolve-TldrEntry -Token $token
    if (-not $entry) { [void]$thin.Add($token); continue }

    # The H1 is the display token for a single-word page ("Get-ChildItem"), but a git-*.md page's
    # H1 is already "git rebase", so the requested token and the heading agree. Where they do not
    # (a page whose H1 carries an alias), the requested token wins: it is what the lookup key is
    # built from, and an entry the service can never find is worse than a slightly off label.
    if ($entry.t -ne $token -and (ConvertTo-PageFileName $entry.t) -ne (ConvertTo-PageFileName $token)) {
        $entry.t = $token
    }

    [void]$entries.Add($entry)
}

# Hand-authored supplement, merged last so it can also override a tldr page if it ever has to.
$supplementCount = 0
if (Test-Path -LiteralPath $SupplementPath) {
    $supplement = Get-Content -LiteralPath $SupplementPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($item in $supplement.entries) {
        $existing = $entries | Where-Object { $_.t -eq $item.t } | Select-Object -First 1
        if ($existing) { [void]$entries.Remove($existing) }

        $entry = [ordered]@{ t = $item.t; d = $item.d }
        if ($item.PSObject.Properties['s'] -and $item.s) { $entry['s'] = $item.s }
        $entry['e'] = @($item.e | ForEach-Object { [PSCustomObject]@{ c = $_.c; d = $_.d } })
        # Marks the entry as authored here rather than derived from tldr-pages, so the CC-BY-SA
        # attribution in the header is a claim about exactly the rows it covers.
        $entry['o'] = 'nova'
        [void]$entries.Add([PSCustomObject]$entry)
        $supplementCount++
    }
}

$ordered = @($entries | Sort-Object -Property t)

# ---------------------------------------------------------------------------------------------
# Emission
# ---------------------------------------------------------------------------------------------

$sourceRevision = 'unknown'
try {
    Push-Location $TldrPath
    $sourceRevision = (& git rev-parse --short HEAD 2>$null)
    if (-not $sourceRevision) { $sourceRevision = 'unknown' }
} catch {
    $sourceRevision = 'unknown'
} finally {
    Pop-Location -ErrorAction SilentlyContinue
}

$catalogue = [ordered]@{
    v = 1
    license = 'CC-BY-SA-4.0'
    licenseUrl = 'https://creativecommons.org/licenses/by-sa/4.0/'
    attribution = 'Command examples from tldr-pages (https://github.com/tldr-pages/tldr), CC BY-SA 4.0. Entries marked "o": "nova" were authored for NovaTerminal and are not tldr-pages content.'
    generatedFrom = "tldr-pages @ $sourceRevision"
    generatedBy = 'scripts/generate-command-catalogue.ps1'
    entries = $ordered
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
    [void](New-Item -ItemType Directory -Path $outputDirectory -Force)
}

# Depth 6 covers header -> entries -> examples -> properties with room to spare. No -Compress: the
# asset is committed, and a diff a reviewer can read is worth more than the ~15% the whitespace
# costs in an already-small file. Written as UTF-8 without a BOM, because System.Text.Json's
# Utf8JsonReader rejects a byte-order mark.
$json = $catalogue | ConvertTo-Json -Depth 6

# ConvertTo-Json escapes the three HTML-significant characters as <, > and & (plus
# ' for the apostrophe) - an injection defence that
# means nothing for a file only System.Text.Json ever reads, and that matters here because every
# argument placeholder in the catalogue is spelled <like_this>. Undoing it costs ~9% of the asset
# and turns an unreadable diff back into a readable one. Safe by inspection: JSON requires no
# escape for any of the three, and the replacement runs over encoder output, so a literal
# backslash in the content is already doubled and cannot be mistaken for an escape prefix.
$json = $json -replace '\\u003c', '<' -replace '\\u003e', '>' -replace '\\u0026', '&' -replace '\\u0027', "'"

[System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))

$bytes = (Get-Item -LiteralPath $OutputPath).Length
$exampleCount = ($ordered | ForEach-Object { $_.e.Count } | Measure-Object -Sum).Sum

Write-Output ''
Write-Output "Catalogue written to $OutputPath"
Write-Output ("  commands : {0}" -f $ordered.Count)
Write-Output ("  examples : {0}" -f $exampleCount)
Write-Output ("  bytes    : {0} ({1:N1} KB)" -f $bytes, ($bytes / 1KB))
Write-Output ("  supplement entries : {0}" -f $supplementCount)
Write-Output ("  tldr revision      : {0}" -f $sourceRevision)

if ($missing.Count -gt 0) {
    Write-Output ''
    Write-Output ("No tldr page for {0} requested command(s):" -f $missing.Count)
    Write-Output ('  ' + ($missing -join ', '))
}
if ($thin.Count -gt 0) {
    Write-Output ''
    Write-Output ("Dropped {0} page(s) with fewer than $MinExamples usable examples:" -f $thin.Count)
    Write-Output ('  ' + ($thin -join ', '))
}

if ($ordered.Count -lt 200) {
    throw "Catalogue has only $($ordered.Count) commands; the plan's floor is 200."
}
if ($bytes -gt 2MB) {
    throw "Catalogue is $bytes bytes; the plan's budget is 2 MB."
}
