"""Call a small checked shipping policy from Python."""

from pathlib import Path

from jacquard_host import Runtime


def main():
    program = Runtime().prepare(Path(__file__).with_name("rules.jac"))
    shipping_cost = program.function("shipping-cost")
    print(f"Order total 35: shipping {shipping_cost(35)}")
    print(f"Order total 75: shipping {shipping_cost(75)}")


if __name__ == "__main__":
    main()
