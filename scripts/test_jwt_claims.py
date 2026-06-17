import urllib.request
import json
import uuid
import sys
import jwt
import datetime

BASE_URL = "http://localhost:5200/api/v1"
JWT_SECRET = "supersecretjwtkeythatisverylongandsecureatleast64characterstowork"
JWT_ISSUER = "WarpTalk.AuthService"
JWT_AUDIENCE = "WarpTalk"

def generate_token(user_id):
    payload = {
        "sub": str(user_id),
        "email": "test@example.com",
        "email_verified": True,
        "iss": JWT_ISSUER,
        "aud": JWT_AUDIENCE,
        "exp": datetime.datetime.now(datetime.UTC) + datetime.timedelta(hours=1)
    }
    return jwt.encode(payload, JWT_SECRET, algorithm="HS256")

user_id = uuid.uuid4()
token = generate_token(user_id)
print(f"Generated token for {user_id}")
