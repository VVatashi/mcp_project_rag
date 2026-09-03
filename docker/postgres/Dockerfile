# PostgreSQL on Alpine with the pgvector extension.
# The Alpine pgvector package targets the distro prefix, so build from source
# against the prefix of this image (/usr/local).
ARG POSTGRES_VERSION=18-alpine
ARG PGVECTOR_VERSION=v0.8.6

FROM postgres:${POSTGRES_VERSION} AS build
ARG PGVECTOR_VERSION
RUN apk add --no-cache build-base clang19 llvm19 git
RUN git clone --depth 1 --branch "${PGVECTOR_VERSION}" https://github.com/pgvector/pgvector.git /tmp/pgvector \
    && cd /tmp/pgvector \
    && make OPTFLAGS="" \
    && make install

FROM postgres:${POSTGRES_VERSION}
COPY --from=build /usr/local/lib/postgresql/vector.so /usr/local/lib/postgresql/
COPY --from=build /usr/local/lib/postgresql/bitcode/vector /usr/local/lib/postgresql/bitcode/vector
COPY --from=build /usr/local/lib/postgresql/bitcode/vector.index.bc /usr/local/lib/postgresql/bitcode/
COPY --from=build /usr/local/share/postgresql/extension/vector.control /usr/local/share/postgresql/extension/
COPY --from=build /usr/local/share/postgresql/extension/vector--*.sql /usr/local/share/postgresql/extension/
