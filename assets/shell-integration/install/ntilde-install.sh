#!/bin/sh
# Ntilde remote shell integration installer (bash and zsh).
#
# Settings copies a one-line command that decodes this file into a temp file, runs it as a CHILD
# process, and deletes it. It is deliberately never sourced into your interactive shell: $1 carries
# the shell name, expanded by the live shell inside that one-liner, so nothing has to be sourced to
# find out which rc file to patch, and nothing this file defines can leak into your session.
#
# It writes ~/.ntilde-shell-integration.sh, adds the loader line to the matching rc file if it is not
# already there, and prints what it did. Running it twice changes nothing the second time.

__ntilde_shell="$1"
if [ -z "$__ntilde_shell" ]; then
    __ntilde_shell=$(basename "${SHELL:-}" 2>/dev/null)
fi

__ntilde_dest="$HOME/.ntilde-shell-integration.sh"

cat > "$__ntilde_dest" <<'__NTILDE_SNIPPET_EOF__'
@@NTILDE_SNIPPET@@
__NTILDE_SNIPPET_EOF__

if [ ! -s "$__ntilde_dest" ]; then
    echo "ntilde: could not write $__ntilde_dest"
    exit 1
fi
echo "ntilde: wrote ~/.ntilde-shell-integration.sh"

case "$__ntilde_shell" in
    zsh)
        __ntilde_rc="$HOME/.zshrc"
        __ntilde_rc_display="~/.zshrc"
        ;;
    bash)
        __ntilde_rc="$HOME/.bashrc"
        __ntilde_rc_display="~/.bashrc"
        ;;
    *)
        __ntilde_rc=""
        __ntilde_rc_display=""
        ;;
esac

__ntilde_loader='[ -f ~/.ntilde-shell-integration.sh ] && . ~/.ntilde-shell-integration.sh'

__ntilde_status=0

if [ -z "$__ntilde_rc" ]; then
    echo "ntilde: could not tell which shell you use - add this line to your rc file:"
    echo "ntilde:   $__ntilde_loader"
# '^[^#]*' rather than a bare substring match: the marker is the file name, so a hand-typed
# variant of the loader line still counts, but a rc file whose only mention is a comment - a
# previous attempt commented out, or a note to self - must not be read as "already installed"
# and left without a loader line while the installer reports success.
elif [ -f "$__ntilde_rc" ] && grep -q '^[^#]*ntilde-shell-integration' "$__ntilde_rc" 2>/dev/null; then
    echo "ntilde: loader line already present in $__ntilde_rc_display - unchanged"
else
    # A rc file that does not end in a newline (common - many editors don't add one) would
    # otherwise get the loader line concatenated onto its last line instead of appended as its
    # own line. Guarded for the common case where the file does not exist yet: tail on a missing
    # file prints nothing to stdout, so this is a no-op and >> below creates it.
    #
    # Both appends are status-checked. A rc file the user cannot write - root-owned, chattr +i,
    # a read-only $HOME on NFS or in a container, a full disk - makes >> fail while the shell
    # carries on to the next command, so an unchecked append prints "added loader line" over the
    # top of the real error and sends the user looking in the right file for a line that was
    # never written.
    __ntilde_appended=1
    if [ -f "$__ntilde_rc" ] && [ -n "$(tail -c1 "$__ntilde_rc" 2>/dev/null)" ]; then
        printf '\n' >> "$__ntilde_rc" || __ntilde_appended=
    fi
    if [ -n "$__ntilde_appended" ] && printf '%s\n' "$__ntilde_loader" >> "$__ntilde_rc"; then
        echo "ntilde: added loader line to $__ntilde_rc_display"
    else
        echo "ntilde: could not write $__ntilde_rc_display - add this line to it by hand:"
        echo "ntilde:   $__ntilde_loader"
        __ntilde_status=1
    fi
fi

echo "ntilde: run  . ~/.ntilde-shell-integration.sh  to enable it in this session,"
echo "ntilde: or open a new Ntilde session to this host."
exit $__ntilde_status
