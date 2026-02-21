package utilities

import (
	"log"
	"os"
	"reflect"
)

type ILogger[T any] interface {
	LogInfo(format string, v ...any)
	LogWarn(format string, v ...any)
	LogError(format string, v ...any)
	LogFatal(format string, v ...any)
}

type logger[T any] struct {
	value    *log.Logger
	typeName string
}

func CreateLogger[T any]() ILogger[T] {
	return logger[T]{
		value:    log.New(os.Stdout, "", log.Ldate|log.Ltime|log.LUTC|log.Lmsgprefix),
		typeName: reflect.TypeFor[T]().String(),
	}
}

func (l logger[T]) LogInfo(format string, v ...any) {
	l.value.Printf("[INFO] [%s] "+format, append([]any{l.typeName}, v...)...)
}

func (l logger[T]) LogWarn(format string, v ...any) {
	l.value.Printf("[WARN] [%s] "+format, append([]any{l.typeName}, v...)...)
}

func (l logger[T]) LogError(format string, v ...any) {
	l.value.Printf("[EROR] [%s] "+format, append([]any{l.typeName}, v...)...)
}

func (l logger[T]) LogFatal(format string, v ...any) {
	l.value.Printf("[FATL] [%s] "+format, append([]any{l.typeName}, v...)...)
}
