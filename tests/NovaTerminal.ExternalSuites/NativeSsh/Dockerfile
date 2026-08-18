# Native SSH end-to-end fixture image.
#
# v3 exists to make the auth rows of docs/native-ssh/Native_SSH_Test_Matrix.md automatable. v2 set
# `PubkeyAuthentication no` and `KbdInteractiveAuthentication no`, so private-key, encrypted-key and
# keyboard-interactive auth could only ever be checked by hand — the matrix listed them as "pending
# manual" because the server refused those methods, not because the tests were hard to write.
FROM debian:12-slim

# socat backs a trivial echo service (see the entrypoint) so forwarding tests can assert that real
# bytes complete the round trip, rather than only that a channel opened.
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-server bash zsh fish ca-certificates vim-tiny socat \
    && rm -rf /var/lib/apt/lists/*

# Two users, because one cannot cover both cases:
#   nova    - password auth (as in v2, so existing tests are unaffected) plus public-key auth.
#   kbdnova - keyboard-interactive ONLY. Without a user that refuses password and pubkey, a client
#             can satisfy the server without ever exercising the challenge-response path.
RUN useradd -m -s /bin/bash nova \
    && echo "nova:nova-pass" | chpasswd \
    && useradd -m -s /bin/bash kbdnova \
    && echo "kbdnova:kbd-pass" | chpasswd

# No key material is generated here, and none is committed either.
#
# The obvious middle ground — generate the test keys during the build — is what the first version of
# this file did, and it is wrong: it puts a private key inside the image, which travels with the image
# to anyone who ever pulls or exports it. SonarCloud flags exactly that ("Credentials should not be
# hard-coded: change this code not to store a secret in the image"), and it is right to.
#
# So the image only prepares the directories. DockerSshFixture generates the keypairs inside the
# running container and copies the private halves out — the container is started with --rm, so the
# keys live and die with one test. See ProvisionTestKeysAsync.
RUN mkdir -p /novaterm-keys \
    && mkdir -p /home/nova/.ssh \
    && chmod 700 /home/nova/.ssh \
    && chown -R nova:nova /home/nova/.ssh

# Global block first, then the Match block: sshd applies Match sections in file order, and anything
# after a Match belongs to that Match until the next one.
RUN mkdir -p /var/run/sshd \
    && printf '%s\n' \
        'PasswordAuthentication yes' \
        'PubkeyAuthentication yes' \
        'KbdInteractiveAuthentication no' \
        'PermitRootLogin no' \
        'UsePAM yes' \
        'AllowTcpForwarding yes' \
        'UseDNS no' \
        'PrintMotd no' \
        'Match User kbdnova' \
        '    PasswordAuthentication no' \
        '    PubkeyAuthentication no' \
        '    KbdInteractiveAuthentication yes' \
        > /etc/ssh/sshd_config.d/novaterm.conf

RUN ssh-keygen -A

RUN printf "export PS1='nova$ '\n" > /home/nova/.bashrc \
    && printf "PROMPT='nova$ '\n" > /home/nova/.zshrc \
    && mkdir -p /home/nova/.config/fish \
    && printf "function fish_prompt\n    printf 'nova$ '\nend\n" > /home/nova/.config/fish/config.fish \
    && printf "if [ -f ~/.bashrc ]; then . ~/.bashrc; fi\n" > /home/nova/.bash_profile \
    && chown -R nova:nova /home/nova \
    && printf "export PS1='nova$ '\n" > /home/kbdnova/.bashrc \
    && printf "if [ -f ~/.bashrc ]; then . ~/.bashrc; fi\n" > /home/kbdnova/.bash_profile \
    && chown -R kbdnova:kbdnova /home/kbdnova

# sshd stays PID 1 via exec, so container stop signals still reach it. The echo service is a
# background child rather than a second supervised process: if it dies the forwarding tests fail
# loudly, which is the correct outcome, and nothing else depends on it.
RUN printf '%s\n' \
        '#!/bin/sh' \
        'set -e' \
        'socat TCP-LISTEN:9001,fork,reuseaddr EXEC:/bin/cat &' \
        'exec /usr/sbin/sshd -D -e' \
        > /usr/local/bin/novaterm-entrypoint.sh \
    && chmod +x /usr/local/bin/novaterm-entrypoint.sh

EXPOSE 22

CMD ["/usr/local/bin/novaterm-entrypoint.sh"]
