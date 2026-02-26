FROM python:3.12-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY . .
ENV API_URL=http://game:8095
ENV MODEL_DIR=/app/models
CMD ["python", "train.py"]
