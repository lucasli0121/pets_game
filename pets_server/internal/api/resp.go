package api

import (
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"

	"pets_server/internal/domain"
)

// Envelope 统一返回结构（文档第 5 节）：{code, msg, data}，code=0 为成功。
type Envelope struct {
	Code int    `json:"code"`
	Msg  string `json:"msg"`
	Data any    `json:"data,omitempty"`
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(v); err != nil {
		log.Printf("[HTTP] 写响应失败: %v", err)
	}
}

func ok(w http.ResponseWriter, data any) {
	writeJSON(w, http.StatusOK, Envelope{Code: domain.CodeOK, Msg: "ok", Data: data})
}

func fail(w http.ResponseWriter, err error) {
	var be *domain.Error
	if errors.As(err, &be) {
		mtr.Business.Add(1)
		status := http.StatusOK
		switch be.Code {
		case domain.CodeUnauthorized:
			status = http.StatusUnauthorized
		case domain.CodeForbidden:
			status = http.StatusForbidden
		}
		writeJSON(w, status, Envelope{Code: be.Code, Msg: be.Msg})
		return
	}
	log.Printf("[HTTP] 未归类错误: %v", err)
	writeJSON(w, http.StatusInternalServerError,
		Envelope{Code: domain.CodeInternal, Msg: "服务内部错误"})
}

// decode 解析请求体，未知字段直接报错（客户端字段拼错时能立刻发现）。
func decode(r *http.Request, v any) error {
	if r.Body == nil {
		return domain.ErrParam("请求体为空")
	}
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if err := dec.Decode(v); err != nil {
		return domain.Errf(domain.CodeParam, "请求体解析失败: %v", err)
	}
	return nil
}

func q(r *http.Request, key, def string) string {
	if v := r.URL.Query().Get(key); v != "" {
		return v
	}
	return def
}

// decodeLoose 同 decode，但允许未知字段。
// 配置快照用这个：管理端可能带上前端自己的 UI 字段，不该因此 500。
func decodeLoose(r *http.Request, v any) error {
	if r.Body == nil {
		return nil
	}
	if err := json.NewDecoder(r.Body).Decode(v); err != nil && err != io.EOF {
		return domain.Errf(domain.CodeParam, "请求体解析失败: %v", err)
	}
	return nil
}

// equalJSON 结构相等判断（用于角色权限里的「字段是否被改动」）。
func equalJSON(a, b any) bool {
	ab, _ := json.Marshal(a)
	bb, _ := json.Marshal(b)
	return string(ab) == string(bb)
}
