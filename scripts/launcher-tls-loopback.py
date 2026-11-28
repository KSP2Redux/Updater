"""Terminate fixture TLS with an owned ephemeral PEM key, forwarding only to loopback."""

import argparse
import json
import select
import socket
import ssl
import sys
import threading


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--certificate", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--backend-port", type=int, required=True)
    args = parser.parse_args()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(args.certificate, args.key)
    stop = threading.Event()
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen()
    listener.settimeout(0.2)

    def connection(client):
        try:
            with client, context.wrap_socket(client, server_side=True) as tls:
                with socket.create_connection(("127.0.0.1", args.backend_port)) as backend:
                    head = bytearray()
                    while not head.endswith(b"\r\n\r\n"):
                        value = tls.recv(1)
                        if not value:
                            return
                        head.extend(value)
                        if len(head) > 65536:
                            raise ValueError("Fixture request headers exceeded their bound")
                    backend.sendall(head)
                    while not stop.is_set():
                        readable, _, _ = select.select([backend], [], [], 0.2)
                        if not readable:
                            continue
                        value = backend.recv(65536)
                        if not value:
                            return
                        tls.sendall(value)
        except (OSError, ssl.SSLError):
            pass

    def lifetime():
        sys.stdin.readline()
        stop.set()

    threading.Thread(target=lifetime, daemon=True).start()
    print(json.dumps({"port": listener.getsockname()[1]}), flush=True)
    try:
        while not stop.is_set():
            try:
                client, _ = listener.accept()
            except TimeoutError:
                continue
            threading.Thread(target=connection, args=(client,), daemon=True).start()
    finally:
        listener.close()


if __name__ == "__main__":
    main()
