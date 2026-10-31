# PostgreSQL on Alpine with pgvector. The official image has no slim tag and no pgvector.
#
# The build turns LLVM off (with_llvm=no). pgvector needs no JIT bitcode, and the clang and llvm packages of Alpine change versions and break the build.
# OPTFLAGS="" drops -march=native, so the image also runs on another machine.

ARG PG_TAG=18.6-alpine3.24

FROM postgres:${PG_TAG} AS build
ARG PGVECTOR_VERSION=0.8.7
RUN apk add --no-cache build-base git
RUN git clone --depth 1 --branch "v${PGVECTOR_VERSION}" https://github.com/pgvector/pgvector.git /src/pgvector
WORKDIR /src/pgvector
RUN make OPTFLAGS="" with_llvm=no \
 && make install with_llvm=no DESTDIR=/out

FROM postgres:${PG_TAG}
COPY --from=build /out/ /
