FROM debian:12-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-server bash ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m -s /bin/bash nova \
    && echo "nova:nova-pass" | chpasswd

RUN mkdir -p /var/run/sshd /home/nova/.ssh \
    && printf '%s\n' \
        'PasswordAuthentication yes' \
        'PermitRootLogin no' \
        'PubkeyAuthentication no' \
        'KbdInteractiveAuthentication no' \
        'ChallengeResponseAuthentication no' \
        'UseDNS no' \
        'PrintMotd no' \
        > /etc/ssh/sshd_config.d/novaterm.conf

RUN ssh-keygen -A

RUN printf "export PS1='nova$ '\n" > /home/nova/.bashrc \
    && printf "if [ -f ~/.bashrc ]; then . ~/.bashrc; fi\n" > /home/nova/.bash_profile \
    && chown -R nova:nova /home/nova

EXPOSE 22

CMD ["/usr/sbin/sshd", "-D", "-e"]
