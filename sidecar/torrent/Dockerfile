# libtorrent sidecar (PoC). Uses the distro's python3-libtorrent (2.0.x) — enough
# to validate the sidecar pattern. Production pins libtorrent 2.1 (bc2df3c) built from source.
#
# The base image is pinned by digest, not just by tag: a tag is a moving pointer, so "bookworm-slim"
# alone means the image CI builds today and the image a contributor builds tomorrow can differ with
# nothing in the diff to show it. Dependabot moves the digest (.github/dependabot.yml) and
# DEPLOYMENT.md records its provenance.
#
# The apt packages below are deliberately NOT version-pinned: Debian's archive drops superseded
# versions, so a pinned apt line breaks the build the first time a security update lands. The digest
# above is what makes the apt set reproducible in practice.
FROM debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251 AS base

RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-libtorrent python3-pip python3-venv ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# A venv that can still see the apt-installed libtorrent in the system site-packages.
RUN python3 -m venv --system-site-packages /venv
ENV PATH="/venv/bin:$PATH"

WORKDIR /app
COPY LICENSE ./LICENSE

COPY requirements.txt ./
# --require-hashes: pip verifies every artifact against requirements.txt before executing any of
# its code, and refuses to install anything the file does not list.
RUN pip install --no-cache-dir --require-hashes -r requirements.txt

COPY proto/ ./proto/
COPY server.py healthcheck.py netguard.py paths.py control_auth.py ./
# Generate the gRPC stubs next to server.py.
RUN python -m grpc_tools.protoc -I proto --python_out=. --grpc_python_out=. proto/torrent.proto

# The suite runs in its own stage, so the test code is executed at build time and shipped nowhere.
# The runtime layer parses hostile swarm data; every file in it that is never executed there is
# audit surface bought for nothing. Build it explicitly with `--target test`.
FROM base AS test
COPY tests/ ./tests/
RUN python -m unittest discover -s tests

# The image that actually runs. It copies from `base`, so nothing above this line brings the tests in.
FROM base AS runtime

# The same uid the application image runs as (its base image's `app` account). Alignment is not
# cosmetic: the sidecar writes the completed download and the application hardlinks it into the
# library, and a link across owners is refused, silently degrading every import into a full copy.
# /app stays owned by root: this process parses hostile swarm data and must not be able to rewrite the
# code that parses it. Only the data root changes hands.
RUN groupadd --gid 1654 sidecar \
    && useradd --uid 1654 --gid 1654 --no-create-home --shell /usr/sbin/nologin sidecar \
    && mkdir -p /data/downloads \
    && chown -R 1654:1654 /data

USER 1654:1654

# The listen address is the deployment's decision, not the image's: the production compose binds the one
# address the application uses. The wildcard default is what the development compose needs to publish the
# port on the host, and it is why that file is development-only.
#
# So is the tunnel. SIDECAR_TUNNEL_DEVICE is deliberately empty here, because an image that assumed a
# tunnel would refuse to run in the default topology, which has none and says so. The VPN overlay is what
# sets it — and once it is set, the policy below applies. `block` is the default for the same reason it is
# the default everywhere else: an installation that opted into a tunnel and then lost it must stop, not
# carry on unprotected.
#
# Two more the deployment sets and this image deliberately does not declare at all:
#   SIDECAR_CONTROL_TOKEN         — the credential on the control port. It is absent rather than empty
#                                   here, because a token in image metadata is a token shipped to
#                                   everybody, and an empty one still teaches a reader that this is
#                                   where you would put it. The server reads it from the environment
#                                   and warns, by name and never by value, when it is unset.
#   SIDECAR_REQUIRE_CONTROL_TOKEN — set to 1 by both deployment files, so no deployed topology can
#                                   start without a credential. Unset only in the PoC compose file.
ENV SIDECAR_LISTEN="[::]:50051" SIDECAR_DATA="/data" \
    SIDECAR_TUNNEL_DEVICE="" SIDECAR_TUNNEL_POLICY="block"
EXPOSE 50051

# There is no HTTP surface to probe, so health is "the gRPC control port accepts a connection at the
# address it was told to bind" — plus, when a tunnel is configured under the `block` policy, "and its
# traffic still leaves through that tunnel". The process refuses to start when it cannot bind, cannot
# write its data root, or cannot verify a tunnel it was told to require, so a container that reports
# healthy is one that can actually take a download and is allowed to.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["python", "healthcheck.py"]

CMD ["python", "server.py"]
