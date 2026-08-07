#!/bin/sh
# Nova Terminal remote shell integration installer (bash and zsh).
#
# Settings copies a one-line command that decodes this file into a temp file, runs it as a CHILD
# process, and deletes it. It is deliberately never sourced into your interactive shell: $1 carries
# the shell name, expanded by the live shell inside that one-liner, so nothing has to be sourced to
# find out which rc file to patch, and nothing this file defines can leak into your session.
#
# It writes ~/.nova-shell-integration.sh, adds the loader line to the matching rc file if it is not
# already there, and prints what it did. Running it twice changes nothing the second time.

__nova_shell="$1"
if [ -z "$__nova_shell" ]; then
    __nova_shell=$(basename "${SHELL:-}" 2>/dev/null)
fi

__nova_dest="$HOME/.nova-shell-integration.sh"

cat > "$__nova_dest" <<'__NOVA_SNIPPET_EOF__'
@@NOVA_SNIPPET@@
__NOVA_SNIPPET_EOF__

if [ ! -s "$__nova_dest" ]; then
    echo "nova: could not write $__nova_dest"
    exit 1
fi
echo "nova: wrote ~/.nova-shell-integration.sh"

case "$__nova_shell" in
    zsh)
        __nova_rc="$HOME/.zshrc"
        __nova_rc_display="~/.zshrc"
        ;;
    bash)
        __nova_rc="$HOME/.bashrc"
        __nova_rc_display="~/.bashrc"
        ;;
    *)
        __nova_rc=""
        __nova_rc_display=""
        ;;
esac

__nova_loader='[ -f ~/.nova-shell-integration.sh ] && . ~/.nova-shell-integration.sh'

if [ -z "$__nova_rc" ]; then
    echo "nova: could not tell which shell you use - add this line to your rc file:"
    echo "nova:   $__nova_loader"
elif [ -f "$__nova_rc" ] && grep -q 'nova-shell-integration' "$__nova_rc" 2>/dev/null; then
    echo "nova: loader line already present in $__nova_rc_display - unchanged"
else
    # A rc file that does not end in a newline (common - many editors don't add one) would
    # otherwise get the loader line concatenated onto its last line instead of appended as its
    # own line. Guarded for the common case where the file does not exist yet: tail on a missing
    # file prints nothing to stdout, so this is a no-op and >> below creates it.
    if [ -f "$__nova_rc" ] && [ -n "$(tail -c1 "$__nova_rc" 2>/dev/null)" ]; then
        printf '\n' >> "$__nova_rc"
    fi
    printf '%s\n' "$__nova_loader" >> "$__nova_rc"
    echo "nova: added loader line to $__nova_rc_display"
fi

echo "nova: run  . ~/.nova-shell-integration.sh  to enable it in this session,"
echo "nova: or open a new Nova session to this host."
