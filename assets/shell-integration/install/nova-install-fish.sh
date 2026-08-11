#!/bin/sh
# Nova Terminal remote shell integration installer (fish).
#
# POSIX sh, not fish: fish cannot parse a heredoc, and the snippet below is data. Run as a child
# process by the one-liner Settings copies, then deleted. $1 is the shell name ("fish"), accepted
# for symmetry with nova-install.sh and unused - conf.d is sourced automatically, so there is no
# rc file to patch and no shell to detect.

__nova_dir="$HOME/.config/fish/conf.d"
if ! mkdir -p "$__nova_dir"; then
    echo "nova: could not create $__nova_dir"
    exit 1
fi

__nova_dest="$__nova_dir/nova-shell-integration.fish"

cat > "$__nova_dest" <<'__NOVA_SNIPPET_EOF__'
@@NOVA_SNIPPET@@
__NOVA_SNIPPET_EOF__

if [ ! -s "$__nova_dest" ]; then
    echo "nova: could not write $__nova_dest"
    exit 1
fi

echo "nova: wrote ~/.config/fish/conf.d/nova-shell-integration.fish"
echo "nova: conf.d is sourced automatically - there is nothing to add to a config file."
echo "nova: run  source ~/.config/fish/conf.d/nova-shell-integration.fish  to enable it in this session,"
echo "nova: or open a new Nova session to this host."
