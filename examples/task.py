import os

from ucl_open_av_dome.task import (
    UclOpenAvDomeTaskLogic,
    UclOpenAvDomeTaskParameters,
)

task_logic = UclOpenAvDomeTaskLogic(
    task_parameters=UclOpenAvDomeTaskParameters(),
)

def main(path_seed: str = "./local/{schema}.json"):
    example_task_logic = task_logic
    os.makedirs(os.path.dirname(path_seed), exist_ok=True)
    models = [example_task_logic]

    for model in models:
        with open(path_seed.format(schema=model.__class__.__name__), "w", encoding="utf-8") as f:
            f.write(model.model_dump_json(indent=2))


if __name__ == "__main__":
    main()