import uuid
import jwt
import datetime

JWT_SECRET = "supersecretjwtkeythatisverylongandsecureatleast64characterstowork"
JWT_ISSUER = "WarpTalk.AuthService"
JWT_AUDIENCE = "WarpTalk"

payload = {
    "sub": str(uuid.uuid4()),
    "email": "test@example.com",
    "email_verified": True,
    "iss": JWT_ISSUER,
    "aud": JWT_AUDIENCE,
    "exp": datetime.datetime.now(datetime.UTC) + datetime.timedelta(hours=1)
}
print(jwt.encode(payload, JWT_SECRET, algorithm="HS256"))
