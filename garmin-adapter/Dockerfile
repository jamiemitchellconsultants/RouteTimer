FROM python:3.12-slim@sha256:229a2c5bfa27522db7815ea81f9bed70af17ccb9de9fc7ad142b1877b5830d36

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

RUN useradd --create-home --uid 10001 adapter

COPY requirements.lock ./

RUN pip install --no-cache-dir --require-hashes -r requirements.lock

COPY pyproject.toml ./
COPY src ./src

RUN pip install --no-cache-dir --no-deps --no-build-isolation .

USER adapter

EXPOSE 8081

CMD ["uvicorn", "routetimer_garmin.api:app", "--host", "0.0.0.0", "--port", "8081", "--no-access-log"]
