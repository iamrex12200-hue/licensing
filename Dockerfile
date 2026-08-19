FROM python:3.12-slim

WORKDIR /app

COPY license_server.py storage.py requirements.txt ./
COPY sentry/ /app/sentry/
COPY templates/ /app/templates/
COPY static/ /app/static/

RUN pip install --no-cache-dir -r requirements.txt \
    && useradd -r -u 10001 licuser && chown -R licuser /app \
    && mkdir -p /var/data && chown licuser /var/data

USER licuser

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/healthz', timeout=3)"

CMD ["python", "license_server.py", "--host", "0.0.0.0", "--port", "8000"]