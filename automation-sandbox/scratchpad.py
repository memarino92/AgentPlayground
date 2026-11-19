"""Private Python interpreter and client inside the isolated runtime container."""
import ast
import contextlib
import io
import json
import os
import socket
import sys
import time
import traceback

SOCKET = "/tmp/scratchpad.sock"


def evaluate(source, namespace):
    tree = ast.parse(source, filename="<cell>")
    tail = tree.body.pop() if tree.body and isinstance(tree.body[-1], ast.Expr) else None
    exec(compile(tree, "<cell>", "exec"), namespace)
    if tail:
        value = eval(compile(ast.Expression(tail.value), "<cell>", "eval"), namespace)
        if value is not None:
            namespace["_"] = value
            print(repr(value))


def save_plot():
    pyplot = sys.modules.get("matplotlib.pyplot")
    if pyplot and pyplot.get_fignums():
        pyplot.gcf().savefig("/artifacts/plot.png", bbox_inches="tight")
        pyplot.close("all")


def serve():
    namespace = {"__name__": "__main__"}
    with socket.socket(socket.AF_UNIX) as server:
        server.bind(SOCKET)
        server.listen(1)
        while True:
            connection, _ = server.accept()
            with connection, connection.makefile("rb") as request, connection.makefile("wb") as reply:
                source = json.loads(request.readline(150000))["source"]
                def emit(kind, value):
                    reply.write((json.dumps([kind, value], ensure_ascii=True) + "\n").encode())
                    reply.flush()

                class Output(io.TextIOBase):
                    def write(self, text):
                        if text:
                            emit("text", text[:32768])
                        return len(text)

                with contextlib.redirect_stdout(Output()), contextlib.redirect_stderr(Output()):
                    try:
                        evaluate(source, namespace)
                        save_plot()
                    except BaseException:
                        traceback.print_exc(limit=8)
                        emit("done", False)
                    else:
                        emit("done", True)


def submit():
    with socket.socket(socket.AF_UNIX) as client:
        for attempt in range(101):
            try:
                client.connect(SOCKET)
                break
            except (FileNotFoundError, ConnectionRefusedError):
                if attempt == 100:
                    raise
                time.sleep(0.05)
        client.sendall(json.dumps({"source": sys.stdin.read()}).encode() + b"\n")
        with client.makefile("rb") as response:
            for line in response:
                kind, value = json.loads(line)
                if kind == "done":
                    if not value:
                        return 42
                    return 3 if os.getenv("SCRATCH_SKIP_EMPTY") and not os.listdir("/artifacts") else 0
                print(value, end="", flush=True)
        return 2


if __name__ == "__main__":
    if sys.argv[1] == "serve":
        serve()
    elif sys.argv[1] == "once":
        with open(sys.argv[2], encoding="utf-8") as program:
            source = program.read()
        namespace = {"__name__": "__main__"}
        marker = "# scratchpad replay v1\n"
        cells = json.loads(source[len(marker):]) if source.startswith(marker) else [source]
        for cell in cells:
            evaluate(cell, namespace)
            save_plot()
    else:
        sys.exit(submit())
