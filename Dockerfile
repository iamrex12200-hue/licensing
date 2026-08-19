FROM python:3.12-slim

WORKDIR /app

COPY license_server.py sentry/ templates/ static/ requirements.txt ./

RUN pip install --no-cache-dir -r requirements.txt \
    && useradd -r -u 10001 licuser && chown -R licuser /app

USER licuser

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/healthz', timeout=3)"

CMD ["python", "license_server.py", "--host", "0.0.0.0", "--port", "8000"]