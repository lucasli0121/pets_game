package api

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"time"
)

// wxSession 微信 jscode2session 返回。
type wxSession struct {
	OpenID     string `json:"openid"`
	UnionID    string `json:"unionid"`
	SessionKey string `json:"session_key"`
	ErrCode    int    `json:"errcode"`
	ErrMsg     string `json:"errmsg"`
}

// wxCode2Session 用 wx.login 的 code 换 openid（小游戏与小程序同一接口）。
func wxCode2Session(ctx context.Context, appID, secret, code string) (*wxSession, error) {
	q := url.Values{}
	q.Set("appid", appID)
	q.Set("secret", secret)
	q.Set("js_code", code)
	q.Set("grant_type", "authorization_code")

	u := "https://api.weixin.qq.com/sns/jscode2session?" + q.Encode()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	cli := &http.Client{Timeout: 6 * time.Second}
	resp, err := cli.Do(req)
	if err != nil {
		return nil, fmt.Errorf("微信登录服务不可用: %w", err)
	}
	defer resp.Body.Close()

	body, _ := io.ReadAll(io.LimitReader(resp.Body, 64*1024))
	var s wxSession
	if err := json.Unmarshal(body, &s); err != nil {
		return nil, fmt.Errorf("微信登录返回解析失败")
	}
	if s.ErrCode != 0 {
		return nil, fmt.Errorf("微信登录失败(%d): %s", s.ErrCode, s.ErrMsg)
	}
	if s.OpenID == "" {
		return nil, fmt.Errorf("微信登录未返回 openid")
	}
	return &s, nil
}
