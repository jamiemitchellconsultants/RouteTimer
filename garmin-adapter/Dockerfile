FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

WORKDIR /app

RUN useradd --create-home --uid 10001 adapter

COPY pyproject.toml ./
COPY src ./src

RUN pip install --no-cache-dir .

USER adapter

EXPOSE 8081

CMD ["uvicorn", "routetimer_garmin.api:app", "--host", "0.0.0.0", "--port", "8081", "--no-access-log"]
