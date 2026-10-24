package store

import "go.mongodb.org/mongo-driver/v2/bson"

// D 构造 bson.D：D("user_id", 1, "created_at", -1)
func D(kv ...any) bson.D {
	d := make(bson.D, 0, len(kv)/2)
	for i := 0; i+1 < len(kv); i += 2 {
		k, ok := kv[i].(string)
		if !ok {
			continue
		}
		d = append(d, bson.E{Key: k, Value: kv[i+1]})
	}
	return d
}

// F 构造 bson.M，支持两种写法：
//
//	F("_id", id, "status", "active")   // 键值对
//	F(D("_id", id), D("points.balance", bson.M{"$gte": 1}))  // 合并多个 bson.D
func F(args ...any) bson.M {
	m := bson.M{}
	allD := len(args) > 0
	for _, a := range args {
		if _, ok := a.(bson.D); !ok {
			allD = false
			break
		}
	}
	if allD {
		for _, a := range args {
			for _, e := range a.(bson.D) {
				m[e.Key] = e.Value
			}
		}
		return m
	}
	for i := 0; i+1 < len(args); i += 2 {
		k, ok := args[i].(string)
		if !ok {
			continue
		}
		m[k] = args[i+1]
	}
	return m
}
