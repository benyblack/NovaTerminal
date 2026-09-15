#!/bin/sh
# Ntilde remote shell integration installer (fish).
#
# POSIX sh, not fish: fish cannot parse a heredoc, and the snippet below is data. Run as a child
# process by the one-liner Settings copies, then deleted. $1 is the shell name ("fish"), accepted
# for symmetry with ntilde-install.sh and unused - conf.d is sourced automatically, so there is no
# rc file to patch and no shell to detect.

__ntilde_dir="$HOME/.config/fish/conf.d"
if ! mkdir -p "$__ntilde_dir"; then
    echo "ntilde: could not create $__ntilde_dir"
    exit 1
fi

__ntilde_dest="$__ntilde_dir/ntilde-shell-integration.fish"

cat > "$__ntilde_dest" <<'__NTILDE_SNIPPET_EOF__'
@@NTILDE_SNIPPET@@
__NTILDE_SNIPPET_EOF__

if [ ! -s "$__ntilde_dest" ]; then
    echo "ntilde: could not write $__ntilde_dest"
    exit 1
fi

echo "ntilde: wrote ~/.config/fish/conf.d/ntilde-shell-integration.fish"
echo "ntilde: conf.d is sourced automatically - there is nothing to add to a config file."
echo "ntilde: run  source ~/.config/fish/conf.d/ntilde-shell-integration.fish  to enable it in this session,"
echo "ntilde: or open a new Ntilde session to this host."
