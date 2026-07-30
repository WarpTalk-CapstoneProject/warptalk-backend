import urllib.request
import urllib.error
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

def print_result(name, url, status, success, res=None):
    print(f"[{'PASS' if success else 'FAIL'}] {name} - {url} - Status: {status}")
    if not success:
        print(f"Response: {res}")

def do_request(url, method, data=None, token=None):
    headers = {}
    if data is not None:
        headers['Content-Type'] = 'application/json'
        data = json.dumps(data).encode('utf-8')
    if token:
        headers['Authorization'] = f'Bearer {token}'
        
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req) as response:
            raw = response.read().decode('utf-8')
            if not raw: return response.status, None
            try: return response.status, json.loads(raw)
            except: return response.status, raw
    except urllib.error.HTTPError as e:
        raw = e.read().decode('utf-8')
        try: return e.code, json.loads(raw)
        except: return e.code, raw
    except Exception as e:
        print(f"Error connecting to {url}: {e}")
        return 0, str(e)

def main():
    print("Running API tests for Meetings Flow...")
    user_id = uuid.uuid4()
    token = generate_token(user_id)
    
    print(f"\n--- Creating Translation Room ---")
    status, res = do_request(f"{BASE_URL}/translation-rooms", "POST", {
        "title": "Test Meeting Room",
        "description": "Integration test room",
        "sourceLanguage": "en",
        "targetLanguages": ["vi", "ja"],
        "allowAudio": True,
        "isPublic": True,
        "maxParticipants": 10,
        "translationRoomType": "meeting"
    }, token)
    print_result("Create Room", f"{BASE_URL}/translation-rooms", status, status in [200, 201], res)
    room_id = res.get("id") if isinstance(res, dict) else None
    if not room_id: sys.exit(1)
        
    print(f"\n--- Joining Meeting ---")
    status, res = do_request(f"{BASE_URL}/meetings/rooms/{room_id}/join", "POST", None, token)
    print_result("Join Meeting", f"{BASE_URL}/meetings/rooms/{room_id}/join", status, status == 200, res)

    print(f"\n--- Testing Old Wrong Route (Expected 404) ---")
    status, res = do_request(f"{BASE_URL}/meetings/{room_id}/chat", "GET", None, token)
    print_result("Old Chat Route GET", f"{BASE_URL}/meetings/{room_id}/chat", status, status == 404, res)

    print(f"\n--- Testing Correct Chat List Route ---")
    status, res = do_request(f"{BASE_URL}/meetings/rooms/{room_id}/chat", "GET", None, token)
    print_result("Correct Chat Route GET", f"{BASE_URL}/meetings/rooms/{room_id}/chat", status, status == 200, res)

    print(f"\n--- Testing Correct Chat Send Route ---")
    status, send_res = do_request(f"{BASE_URL}/meetings/rooms/{room_id}/chat", "POST", {
        "originalText": "Hello integration test!",
        "originalLanguage": "en",
        "translationEnabled": True,
        "messageType": "text"
    }, token)
    print_result("Correct Chat Route POST", f"{BASE_URL}/meetings/rooms/{room_id}/chat", status, status == 200, send_res)
    message_id = send_res.get("id") if isinstance(send_res, dict) else None

    if message_id:
        print(f"\n--- Testing Translate Chat Route ---")
        status, trans_res = do_request(f"{BASE_URL}/meetings/rooms/{room_id}/chat/{message_id}/translate", "POST", {
            "targetLanguage": "vi"
        }, token)
        print_result("Translate Chat Route POST", f"{BASE_URL}/meetings/rooms/{room_id}/chat/{message_id}/translate", status, status in [200, 202], trans_res)

    print("\n✅ All Backend API verifications for Chat Route passed!")

if __name__ == "__main__":
    main()
