# python:alpine rather than a .NET image: this is 150 lines that must be auditable at a glance,
# and it holds the only write access to Docker's container directory in the whole stack. A
# smaller attack surface matters more here than consistency with the other services.
# Built from the REPOSITORY ROOT, like every other image here: .gitlab-ci.yml's build job
# always passes `.` as the context, so a Dockerfile expecting its own directory would work in
# compose and fail in CI.
FROM python:3.13-alpine
COPY infra/log-janitor/janitor.py /janitor.py
# Runs as root, unavoidably: the json log files are root-owned and truncating one needs write
# permission on it. Every other privilege is dropped in compose (cap_drop: ALL, no-new-privileges,
# read-only root filesystem).
EXPOSE 8080
ENTRYPOINT ["python3", "/janitor.py"]
