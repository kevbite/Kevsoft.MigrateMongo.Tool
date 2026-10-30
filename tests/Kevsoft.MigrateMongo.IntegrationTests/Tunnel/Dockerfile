# A minimal SSH bastion for the tunnel integration tests: one ed25519 host key (so its fingerprint is predictable),
# TCP forwarding enabled, and a 'tunnel' user that accepts a password or the generated key in /keys.
FROM alpine:3.22

RUN apk add --no-cache openssh-server \
 && ssh-keygen -q -t ed25519 -N "" -f /etc/ssh/ssh_host_ed25519_key \
 && adduser -D -s /bin/sh tunnel \
 && echo "tunnel:tunnel-password" | chpasswd \
 && mkdir -p /keys /home/tunnel/.ssh \
 && ssh-keygen -q -t ed25519 -N "" -C tunnel-test -f /keys/id_ed25519 \
 && cp /keys/id_ed25519.pub /home/tunnel/.ssh/authorized_keys \
 && chown -R tunnel:tunnel /home/tunnel/.ssh \
 && chmod 700 /home/tunnel/.ssh \
 && chmod 600 /home/tunnel/.ssh/authorized_keys \
 && chmod 644 /keys/id_ed25519 \
 && printf '%s\n' \
      'Port 22' \
      'HostKey /etc/ssh/ssh_host_ed25519_key' \
      'PermitRootLogin no' \
      'PubkeyAuthentication yes' \
      'PasswordAuthentication yes' \
      'KbdInteractiveAuthentication no' \
      'AllowTcpForwarding yes' \
      'GatewayPorts no' \
      'X11Forwarding no' \
      'PidFile /run/sshd.pid' \
      > /etc/ssh/sshd_config

EXPOSE 22

CMD ["/usr/sbin/sshd", "-D", "-e"]
