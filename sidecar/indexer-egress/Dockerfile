FROM debian:bookworm-slim@sha256:7b140f374b289a7c2befc338f42ebe6441b7ea838a042bbd5acbfca6ec875818 AS base

RUN apt-get update && apt-get install -y --no-install-recommends python3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY LICENSE ./LICENSE
COPY proxy.py ./

FROM base AS test
COPY tests/ ./tests/
RUN python3 -m unittest discover -s tests

FROM base AS runtime
USER 65532:65532
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
    CMD ["python3", "proxy.py", "--healthcheck"]
CMD ["python3", "proxy.py"]
