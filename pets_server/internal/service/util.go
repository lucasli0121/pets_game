package service

import (
	"crypto/rand"
	"encoding/hex"
	"strconv"
	"time"

	"pets_server/internal/domain"
)

// newID 生成 24 位十六进制 id（与 Mongo ObjectId 长度一致，便于日志对齐）。
func newID() string {
	b := make([]byte, 12)
	if _, err := rand.Read(b); err != nil {
		return strconv.FormatInt(time.Now().UnixNano(), 16)
	}
	return hex.EncodeToString(b)
}

// newOrderID 订单号：PO + 时间 + 随机，便于运营肉眼核对。
func newOrderID() string {
	return "PO" + time.Now().Format("20060102150405") + newID()[:6]
}

// toError 把任意 error 归一为业务错误，未识别的按内部错误处理。
func toError(err error) error {
	if err == nil {
		return nil
	}
	if _, ok := err.(*domain.Error); ok {
		return err
	}
	return domain.Err(domain.CodeInternal, err.Error())
}
