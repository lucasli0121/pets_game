package auth

import (
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"time"

	"golang.org/x/crypto/bcrypt"
)

var ErrInvalidToken = errors.New("invalid token")

// Claims 载荷。Kind 区分客户端与管理员两类 token，防止越权互换。
type Claims struct {
	Sub  string `json:"sub"`  // user_id 或 admin_id
	Kind string `json:"kind"` // client / admin
	Role string `json:"role,omitempty"`
	Name string `json:"name,omitempty"`
	Exp  int64  `json:"exp"`
	Iat  int64  `json:"iat"`
}

type header struct {
	Alg string `json:"alg"`
	Typ string `json:"typ"`
}

// Sign 生成 HS256 token。不引第三方 JWT 库，减少依赖与二进制体积。
func Sign(secret string, c Claims) (string, error) {
	if c.Iat == 0 {
		c.Iat = time.Now().Unix()
	}
	if c.Exp == 0 {
		return "", errors.New("exp required")
	}
	h, _ := json.Marshal(header{Alg: "HS256", Typ: "JWT"})
	p, err := json.Marshal(c)
	if err != nil {
		return "", err
	}
	signing := b64(h) + "." + b64(p)
	sig := sign(secret, signing)
	return signing + "." + sig, nil
}

// Parse 校验签名与过期时间。
func Parse(secret, token string) (*Claims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return nil, ErrInvalidToken
	}
	signing := parts[0] + "." + parts[1]
	want := sign(secret, signing)
	if subtle.ConstantTimeCompare([]byte(want), []byte(parts[2])) != 1 {
		return nil, ErrInvalidToken
	}
	raw, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return nil, ErrInvalidToken
	}
	var c Claims
	if err := json.Unmarshal(raw, &c); err != nil {
		return nil, ErrInvalidToken
	}
	if c.Exp > 0 && time.Now().Unix() > c.Exp {
		return nil, ErrInvalidToken
	}
	return &c, nil
}

func b64(b []byte) string { return base64.RawURLEncoding.EncodeToString(b) }

func sign(secret, data string) string {
	m := hmac.New(sha256.New, []byte(secret))
	m.Write([]byte(data))
	return b64(m.Sum(nil))
}

// ---- 密码 ----

func HashPassword(pw string) (string, error) {
	h, err := bcrypt.GenerateFromPassword([]byte(pw), bcrypt.DefaultCost)
	if err != nil {
		return "", err
	}
	return string(h), nil
}

func CheckPassword(hash, pw string) bool {
	return bcrypt.CompareHashAndPassword([]byte(hash), []byte(pw)) == nil
}
