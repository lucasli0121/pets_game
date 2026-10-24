package domain

import "fmt"

// 业务错误码。code=0 为成功（文档第 5 节统一约定）。
const (
	CodeOK = 0

	CodeParam        = 1001 // 参数非法
	CodeUnauthorized = 1002 // 未登录 / token 无效
	CodeForbidden    = 1003 // 越权
	CodeNotFound     = 1004 // 资源不存在
	CodeRateLimited  = 1005 // 频控触发
	CodeInternal     = 1006 // 服务内部错误
	CodeProcessing   = 1007 // 同一 request_id 正在处理中

	CodePointsNotEnough = 2001 // 积分不足
	CodeSoldOut         = 2002 // 商品已下架 / 不存在
	CodeNoInventory     = 2003 // 库存不足
	CodeAdLimit         = 2101 // 广告奖励已达当日上限
	CodeAdTooFrequent   = 2102 // 广告间隔过短
	CodeAdDisabled      = 2103 // 广告位未启用
	CodeAlreadyClaimed  = 2201 // 已签到
	CodeAlreadyOwned    = 2202 // 已拥有（场景 / 宠物）
	CodeCooldown        = 2203 // 冷却中（性格修改）
	CodePetLimit        = 2301 // 宠物数量已达上限
	CodeNoActivePet     = 2302 // 无激活宠物
	CodeBadPersonality  = 2303 // 性格维度越界
)

// Error 业务错误。Handle 层据此生成 {code,msg,data}。
type Error struct {
	Code int
	Msg  string
}

func (e *Error) Error() string { return fmt.Sprintf("%d:%s", e.Code, e.Msg) }

func Err(code int, msg string) *Error { return &Error{Code: code, Msg: msg} }

func Errf(code int, format string, a ...any) *Error {
	return &Error{Code: code, Msg: fmt.Sprintf(format, a...)}
}

// 常用快捷构造
func ErrParam(msg string) *Error     { return Err(CodeParam, msg) }
func ErrNotFound(msg string) *Error  { return Err(CodeNotFound, msg) }
func ErrForbidden(msg string) *Error { return Err(CodeForbidden, msg) }
func ErrInternal(msg string) *Error  { return Err(CodeInternal, msg) }
func ErrPointsNotEnough() *Error     { return Err(CodePointsNotEnough, "积分不足") }
func ErrNoInventory() *Error         { return Err(CodeNoInventory, "库存不足") }
