FROM ghcr.io/flaresolverr/flaresolverr:v3.4.5@sha256:4f4e5f759aa3a9a64305e99188ea1db1ec2944a5e7d290d2b089af5f2f6f48e4

USER root

# Chrome otherwise bypasses configured proxies for loopback and link-local destinations.
RUN sed -i "/options.add_argument('--proxy-server=%s' % proxy_url)/a\\        options.add_argument('--proxy-bypass-list=<-loopback>')" /app/utils.py \
    && grep -Fq "options.add_argument('--proxy-bypass-list=<-loopback>')" /app/utils.py

USER flaresolverr
