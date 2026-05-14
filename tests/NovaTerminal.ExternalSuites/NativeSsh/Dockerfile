FROM debian:12-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-server bash zsh fish ca-certificates vim-tiny \
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
    && printf "PROMPT='nova$ '\n" > /home/nova/.zshrc \
    && mkdir -p /home/nova/.config/fish \
    && printf "function fish_prompt\n    printf 'nova$ '\nend\n" > /home/nova/.config/fish/config.fish \
    && printf "if [ -f ~/.bashrc ]; then . ~/.bashrc; fi\n" > /home/nova/.bash_profile \
    && chown -R nova:nova /home/nova

EXPOSE 22

CMD ["/usr/sbin/sshd", "-D", "-e"]
