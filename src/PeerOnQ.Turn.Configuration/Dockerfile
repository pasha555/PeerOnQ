FROM coturn/coturn:4.15.0-r0@sha256:0feee4fc1f45c7c053c8fee3e1ab941b1a1b9a0429bc01e18126735410770bfd

COPY turnserver.conf /etc/coturn/turnserver.conf
COPY --chmod=0555 docker-entrypoint.sh /usr/local/bin/peeronq-turn-entrypoint

EXPOSE 3478/tcp 3478/udp 5349/tcp 5349/udp 49160-49200/udp 9641/tcp
HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD turnutils_stunclient -p 3478 127.0.0.1 >/dev/null 2>&1 || exit 1

ENTRYPOINT ["/usr/local/bin/peeronq-turn-entrypoint"]
